# MedMission Bridge — Integration Spec

For the medical-imaging software team. Everything here is implemented and covered by
the bridge's test suite; nothing in this document is planned-but-missing unless it is
listed under [Open items](#12-open-items).

Two integration surfaces:

1. **DICOM Modality Worklist (C-FIND SCP)** — pull the scheduled patient into the
   X-ray console so the operator does not retype demographics.
2. **HTTP REST** — pull the full survey answers (symptoms, TB history, vital signs)
   for a study you already have, keyed by Patient ID or Accession Number.

---

## 1. Topology

    [Android tablet] --HTTP/JSON--> [Bridge (field laptop)] <--DICOM C-FIND-- [X-ray console]
                                            |                                  your software
                                            +--HTTP/JSON---------------------> your software

The bridge is a self-contained Windows executable on the field laptop. It owns a local
SQLite database; there is no server and no internet dependency. Tablets push surveys in;
you pull them out. The bridge never calls your software.

## 2. Endpoints and ports

| Surface | Default | Configured by |
|---|---|---|
| REST (LAN-facing) | TCP `18080` | `Bridge:HttpPort` |
| DICOM MWL SCP | TCP `11112` | `Bridge:Mwl:Port` |
| mDNS advertisement | `_medmission._tcp` on the HTTP port | `Bridge:Mdns:*` |

**Confirm the MWL port per laptop before you hardcode it.** Windows reserves TCP ranges
for Hyper-V/WinNAT and `11112` falls inside one on some machines. Where that happens the
site runs on `12112`. Ask the operator, or read `mwlPort` from the laptop's health panel
at `http://127.0.0.1:18080/`.

The management UI (`/api/ui/*` and the web pages) is **loopback-only** — requests from
the LAN get `403`. Do not build against it; it is an operator tool, not an interface.
The `/api/v1/*` surface is the contract.

## 3. DICOM MWL — association

| Property | Value |
|---|---|
| Called AE Title | `MEDMISSION` (`Bridge:Mwl:AeTitle`) |
| AE title enforcement | **None.** Any calling and called AE is accepted |
| Accepted SOP classes | Verification (`1.2.840.10008.1.1`), Modality Worklist Information Model – FIND (`1.2.840.10008.5.1.4.31`) |
| Transfer syntaxes | Explicit VR Little Endian, Implicit VR Little Endian |
| Specific Character Set | `ISO_IR 192` (UTF-8) on every returned item |
| Other SOP classes | Rejected with *abstract syntax not supported* |

C-ECHO is answered with `Success`, so you can verify connectivity before wiring the
worklist. Every association request is logged with calling AE, called AE and remote host,
which is the fastest way to prove your console reached the bridge at all.

## 4. DICOM MWL — matching keys

Supported query keys. **Any key not in this table is ignored**, not rejected — a query
carrying only unsupported keys returns every scheduled item.

| Key | Tag | Matching |
|---|---|---|
| Patient ID | `(0010,0020)` | Exact, case-sensitive |
| Patient Name | `(0010,0010)` | Wildcard `*` and `?`, case-**in**sensitive |
| Modality | `(0008,0060)` inside SPS sequence | Exact |
| Scheduled Procedure Step Start Date | `(0040,0002)` inside SPS sequence | Exact `YYYYMMDD`, or range `YYYYMMDD-YYYYMMDD`, or open range `YYYYMMDD-` / `-YYYYMMDD` |

Empty or absent keys are treated as "match everything", per the DICOM matching rules.
The usual console query — today's date plus modality — works unchanged.

## 5. DICOM MWL — returned item

The bridge returns the full item dataset regardless of which return keys you request.

| Attribute | Tag | Source | Present when |
|---|---|---|---|
| Specific Character Set | `(0008,0005)` | fixed `ISO_IR 192` | always |
| Patient Name | `(0010,0010)` | `lastName^firstName` | either name present |
| Patient ID | `(0010,0020)` | `recordId` | **always** |
| Patient Birth Date | `(0010,0030)` | `patient.birthDate` converted to `YYYYMMDD` | parseable date present |
| Patient Sex | `(0010,0040)` | `MALE` becomes `M`, `FEMALE` becomes `F` | gender present |
| Accession Number | `(0008,0050)` | `no` | `no` present |
| Scheduled Procedure Step Sequence | `(0040,0100)` | one item, below | always |
| — SPS Start Date | `(0040,0002)` | survey `date`, else today | always |
| — SPS Start Time | `(0040,0003)` | local time the survey arrived, `HHMMSS` | always |
| — Modality | `(0008,0060)` | `Bridge:Mwl:Modality`, default `CR` | always |
| — Scheduled Station AE Title | `(0040,0001)` | `Bridge:Mwl:StationAeTitle` | always |
| — SPS Description | `(0040,0007)` | `Bridge:Mwl:ProcedureDescription` | always |

**Patient Name is not always present, Patient ID always is.** Key your side on Patient ID.
Study Instance UID, Requested Procedure ID and Scheduled Procedure Step ID are **not**
supplied — see [Open items](#12-open-items) if your console requires them.

## 6. Which records appear on the worklist

A survey holds one of four statuses. The worklist returns `Received` and `InProgress`
only, newest arrival first.

| Status | On worklist | Set by |
|---|---|---|
| `Received` | yes | tablet send (initial) |
| `InProgress` | yes | operator, in the bridge UI |
| `Completed` | **no** | operator, in the bridge UI |
| `Cancelled` | **no** | operator, in the bridge UI |

Two consequences worth designing around:

- A tablet edit-and-resend of an already-completed survey refreshes the demographics but
  **does not** move it back onto the worklist. Completed studies do not reappear.
- Status is moved by a human in the bridge UI, not by your software and not by the
  arrival of an image. If nothing marks studies complete, the worklist grows all day.
  Tell us if you would rather drive status over an API — that is a small addition.

If the worklist cannot be read from the database, the bridge answers the C-FIND with
`ProcessingFailure` rather than an empty success, so an infrastructure fault is
distinguishable from "no patients scheduled".

## 7. REST — authentication

Every `/api/v1/*` request needs a shared secret:

    X-Api-Key: <the site's key>

Exact string match, no scheme prefix. Missing or wrong key gives `401` with no body. It
is the same key the tablets use, and it is **per-laptop**: unless the site set one in
`appsettings.json`, each bridge generates its own on first start, in the form
`XXXXX-XXXXX-XXXXX-XXXXX`. Read it from the site operator or from the laptop's own
management page. Never hardcode a key, and expect it to differ between laptops.

Transport is plain HTTP. The deployment premise is an isolated field network with no
route to the internet — see [Open items](#12-open-items).

## 8. REST — endpoints

### `GET /api/v1/surveys/{recordId}`

Fetch one survey by Patient ID (the `recordId` from the worklist).

    200  application/json; charset=utf-8   the survey, verbatim as the tablet sent it
    401  wrong or missing X-Api-Key
    404  no such recordId

### `GET /api/v1/surveys?accession={no}`

Fetch one survey by Accession Number, for when you hold the accession from the image
header rather than the patient ID.

    200  application/json; charset=utf-8   the survey, verbatim
    400  accession parameter missing or blank
    401  wrong or missing X-Api-Key
    404  no survey with that accession

Accession is **not** guaranteed unique in the database. If several rows share one, the
most recently updated wins. Patient ID is the unique key; prefer it where you have it.

### `POST /api/v1/surveys`

The tablet's ingest endpoint. Documented so you can simulate a tablet during testing;
your software has no reason to call it in production.

    body: the survey JSON, must be a JSON object with a non-empty "recordId"
    200  stored (insert or update)
    400  malformed JSON, non-object body, or missing recordId
    401  wrong or missing X-Api-Key

Upsert semantics keyed on `recordId`: re-sending the same record is safe and idempotent,
and never rewrites `status` or the original arrival time.

## 9. Survey JSON

The response body is the exact bytes the tablet posted — the bridge stores the raw
payload and replays it. The authoritative field-by-field reference is the tablet repo's
`docs/reference/wire-contract.md`, which is generated from the tablet DTOs; this section
is the working summary.

Two parsing rules matter more than the field list, both consequences of how the tablet
serializes:

1. **An unanswered field is absent, not `null`.** Do not expect `"gender": null` — expect
   no `gender` key at all. A survey is filled in the field under time pressure, so
   missing keys are the normal case, not an error.
2. **The nine top-level keys are always present**, except `no` and `date`, which follow
   rule 1. The nested objects (`patient`, `medicalHistory`, …) are emitted even when
   empty, and `symptoms` is emitted even when empty.

A completely blank survey is therefore exactly:

```json
{"recordId":"6f1e...","patient":{},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}
```

Extra fields may appear as the tablet evolves; parse leniently. A fully populated survey:

```json
{
  "recordId": "8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33",
  "no": "TAB-A3F2-0007",
  "date": "2026-08-18",
  "patient": {
    "firstName": "Juan", "lastName": "Dela Cruz",
    "birthDate": "1984-03-12", "gender": "MALE", "age": 42,
    "address": "12 Mabini St", "region": "REGION IV-A", "province": "Laguna",
    "city": "Calamba", "barangay": "Real", "zip": "4027",
    "email": "juan@example.com", "cellPhone": "0917-000-0000",
    "maritalStatus": "MARRIED"
  },
  "medicalHistory": {
    "items": ["HYPERTENSION", "DIABETES"],
    "others": "Migraine",
    "recentSurgeriesOrHospitalization": "Appendectomy 2019",
    "currentMedication": "Metformin 500mg"
  },
  "vitalSigns": {
    "height": 168.0, "weight": 61.5,
    "bpSystolic": 130, "bpDiastolic": 85,
    "pulseRate": 78, "respiratoryRate": 18,
    "temperature": 36.8, "oxygenSaturation": 98.0, "bloodGlucose": 105.0
  },
  "symptoms": ["COUGH_2WEEKS_PLUS", "NIGHT_SWEATS"],
  "tbInfo": {
    "everDiagnosedTB": "NO",
    "everReceivedTreatment": "NO",
    "closeContactActiveTB": "YES", "closeContactWhen": "2025",
    "householdMemberTBTreatment": "DONT_KNOW"
  },
  "smoking": { "status": "FORMER", "duration": "FIVE_TO_10" },
  "alcohol": { "drinks": true, "amount": "ONE_TO_TWO" },
  "environmentalExposure": {
    "dustSmokeChemicalExposure": true, "cooksWithSolidFuels": true,
    "secondhandSmokeExposure": false, "crowdedLivingConditions": false
  }
}
```

### Enumerated values

Enums travel as the symbolic name, never the display label, never an integer. Treat an
unknown value as "not answered" rather than failing the parse — new options can be added
to the form.

| Field | Values |
|---|---|
| `patient.gender` | `MALE`, `FEMALE` |
| `patient.maritalStatus` | `MARRIED`, `SINGLE`, `DIVORCED`, `WIDOWED`, `OTHER` |
| `medicalHistory.items[]` | `HYPERTENSION`, `DIABETES`, `ASTHMA`, `HEART_DISEASE`, `KIDNEY_DISEASE`, `STROKE`, `TUBERCULOSIS`, `CANCER`, `ALLERGIES` |
| `symptoms[]` | `COUGH`, `COUGH_2WEEKS_PLUS`, `SPUTUM`, `BLOOD_IN_SPUTUM`, `FEVER`, `CHEST_PAIN`, `SHORTNESS_OF_BREATH`, `WEIGHT_LOSS`, `NIGHT_SWEATS`, `FATIGUE`, `NONE` |
| `tbInfo` yes/no fields | `YES`, `NO`, `DONT_KNOW` |
| `smoking.status` | `NEVER`, `CURRENT`, `FORMER` |
| `smoking.duration` | `NONE`, `LESS_THAN_5`, `FIVE_TO_10`, `MORE_THAN_10` |
| `alcohol.amount` | `ONE_TO_TWO`, `THREE_TO_FOUR`, `FIVE_PLUS` |

`maritalStatus: "OTHER"` puts the free text in `maritalStatusOther`; `medicalHistory.others`
works the same way. `symptoms: ["NONE"]` is an explicit "no symptoms" answer and is not
the same as an empty array, which means the question was skipped.

### Units and formats

| Field | Format |
|---|---|
| `date`, `patient.birthDate` | ISO-8601 `YYYY-MM-DD`, tablet's local date |
| `height` | cm |
| `weight` | kg |
| `temperature` | degrees Celsius |
| `bpSystolic`, `bpDiastolic` | mmHg |
| `oxygenSaturation` | percent |
| `bloodGlucose` | mg/dL |
| `recordId` | UUID v4 string, generated on the tablet |
| `no` | `TAB-<device>-<0000>`, e.g. `TAB-A3F2-0007` |

Dates on the wire are ISO-8601; the DICOM `DA` form (`YYYYMMDD`) exists only inside
worklist responses. Text may contain non-ASCII characters — the survey is UTF-8
throughout, and the worklist declares `ISO_IR 192` for the same reason.

## 10. Identifier mapping

| Concept | DICOM | JSON | Notes |
|---|---|---|---|
| Patient identity | Patient ID `(0010,0020)` | `recordId` | UUID, unique, always present |
| Study/visit number | Accession Number `(0008,0050)` | `no` | Human-readable, printed on the form, not guaranteed unique |

The intended flow:

1. Console pulls the worklist, operator picks the patient, shoots the image.
2. Your software reads Patient ID (or Accession Number) off the acquired image.
3. Your software calls `GET /api/v1/surveys/{PatientID}` and attaches the answers to the
   study — symptoms and TB history are the fields the screening reading depends on.

## 11. Verifying the link

C-ECHO, then a worklist query (`findscu` from the DCMTK toolkit; substitute your own):

    echoscu -aec MEDMISSION <laptop-ip> 11112

    findscu -W -k "(0008,0005)=ISO_IR 192" \
            -k "ScheduledProcedureStepSequence[0].Modality=CR" \
            -k "ScheduledProcedureStepSequence[0].ScheduledProcedureStepStartDate=20260818" \
            -aec MEDMISSION <laptop-ip> 11112

REST:

    curl -H "X-Api-Key: <key>" http://<laptop-ip>:18080/api/v1/surveys/8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33
    curl -H "X-Api-Key: <key>" "http://<laptop-ip>:18080/api/v1/surveys?accession=TAB-A3F2-0007"

If the worklist is empty, check that at least one survey sits in `Received` or
`InProgress` on the bridge UI before suspecting the network.

## 12. Open items

Points where we need a decision from your side. None of them block a read-only
integration today.

1. **Study Instance UID / Requested Procedure ID.** Not currently emitted. Some consoles
   require them to build the study. If yours does, tell us the attributes and we will
   generate and return them.
2. **Who marks a study complete.** Today an operator does it in the bridge UI. If your
   software knows when acquisition finished, a status-change API is the better source of
   truth. Small change on our side, needs your trigger.
3. **AE title enforcement.** Currently any AE is accepted, logged but not checked. Say
   the word and we will restrict to your calling AE.
4. **TLS.** Deferred by agreement on the premise of an isolated field network. If the
   laptop will ever sit on a routed hospital network, this needs revisiting before
   go-live — the API key crosses the wire in plaintext today.
5. **Accession uniqueness.** `no` is assembled on the tablet from a device prefix and a
   counter; two tablets with the same prefix can collide. If your side keys on accession,
   we should tighten the format.
