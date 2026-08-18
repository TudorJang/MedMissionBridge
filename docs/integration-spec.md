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
for Hyper-V/WinNAT and `11112` falls inside one on some machines; the bridge detects this
and the site then runs on `12112` or similar. Ask the operator, who can read the port off
the laptop's management page at `http://127.0.0.1:18080/`.

The management UI (`/api/ui/*` and the web pages) is **loopback-only** — requests from
the LAN get `403`. Do not build against it; it is an operator tool, not an interface.
The `/api/v1/*` surface is the contract.

## 3. DICOM MWL — association

| Property | Value |
|---|---|
| Called AE Title | `MEDMISSION` (`Bridge:Mwl:AeTitle`) |
| AE title enforcement | **None.** Any calling and called AE is accepted |
| Accepted SOP classes | Verification (`1.2.840.10008.1.1`), Modality Worklist Information Model – FIND (`1.2.840.10008.5.1.4.31`), Modality Performed Procedure Step (`1.2.840.10008.3.1.2.3.3`) |
| Transfer syntaxes | Explicit VR Little Endian, Implicit VR Little Endian |
| Specific Character Set | `ISO_IR 192` (UTF-8) on every returned item |
| Other SOP classes | Rejected with *abstract syntax not supported* |

**Worklist and MPPS share one host, port and AE title.** Point both destinations in the
console's network settings at the same place.

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
| Accession Number | `(0008,0050)` | Wildcard `*` and `?`, case-**in**sensitive |
| Modality | `(0008,0060)` inside SPS sequence | Exact |
| Scheduled Procedure Step Start Date | `(0040,0002)` inside SPS sequence | Exact `YYYYMMDD`, or range `YYYYMMDD-YYYYMMDD`, or open range `YYYYMMDD-` / `-YYYYMMDD` |

Empty or absent keys are treated as "match everything", per the DICOM matching rules.
The usual console query — today's date plus modality — works unchanged.

Accession is deliberately forgiving on case and accepts wildcards, because it is the
number printed on the form in the patient's hand and gets typed at a console with a long
list already on screen. A record with no accession never matches an accession query.

## 5. DICOM MWL — returned item

The bridge returns the full item dataset regardless of which return keys you request.

| Attribute | Tag | Source | Present when |
|---|---|---|---|
| Specific Character Set | `(0008,0005)` | fixed `ISO_IR 192` | always |
| Patient Name | `(0010,0010)` | `lastName^firstName` | either name present |
| Patient ID | `(0010,0020)` | `recordId` | **always** |
| Patient Birth Date | `(0010,0030)` | `patient.birthDate` converted to `YYYYMMDD` | parseable date present |
| Patient Sex | `(0010,0040)` | `MALE` becomes `M`, `FEMALE` becomes `F` | gender present |
| Patient Age | `(0010,1010)` | derived from birth date, `nnnY` | parseable birth date |
| Patient Address | `(0010,1040)` | street, barangay, city, province, region, zip | any present |
| Accession Number | `(0008,0050)` | `no` | `no` present |
| Referring Physician | `(0008,0090)` | `Bridge:Mwl:ReferringPhysician` | configured |
| Study Instance UID | `(0020,000D)` | derived from `recordId`, arc `2.25` | **always** |
| Study Description | `(0008,1030)` | survey summary, below | always |
| Requested Procedure Description | `(0032,1060)` | survey summary, below | always |
| Requested Procedure ID | `(0040,1001)` | `no`, truncated to 16 | `no` present |
| Scheduled Procedure Step Sequence | `(0040,0100)` | one item, below | always |
| — SPS Start Date | `(0040,0002)` | survey `date`, else today | always |
| — SPS Start Time | `(0040,0003)` | local time the survey arrived, `HHMMSS` | always |
| — Modality | `(0008,0060)` | `Bridge:Mwl:Modality`, default `CR` | always |
| — Scheduled Station AE Title | `(0040,0001)` | `Bridge:Mwl:StationAeTitle` | always |
| — SPS Description | `(0040,0007)` | survey summary, below | always |
| — SPS ID | `(0040,0009)` | `no`, truncated to 16 | `no` present |
| — SPS Status | `(0040,0020)` | `SCHEDULED` | always |

The Study Instance UID is derived from the record id rather than stored, so repeated
queries for the same survey always return the same UID. It uses the `2.25` arc, which is
reserved for UUID-derived identifiers, so no organisation root has to be registered.

### The whole survey rides in the worklist item

The console cannot call the REST API, and whatever the worklist carries is what the
console copies into the acquired image and forwards to PACS. So the complete survey is in
the worklist item, in three layers. Nothing is dropped at any layer; the private element
is the one that is lossless.

**Layer 1 — standard attributes**, so an ordinary DICOM reader gets them for free:

| Attribute | Tag | Source | Note |
|---|---|---|---|
| Patient's Telephone Numbers | `(0010,2154)` | `patient.cellPhone` | |
| Patient's Size | `(0010,1020)` | `vitalSigns.height` | **metres** — the survey is in cm |
| Patient's Weight | `(0010,1030)` | `vitalSigns.weight` | kg |
| Smoking Status | `(0010,21A0)` | `smoking.status` | `CURRENT`→`YES`, `NEVER`→`NO`. A former smoker is **omitted**: the attribute asks whether the patient smokes now, and neither value is true of them |

**Layer 2 — Additional Patient History `(0010,21B0)`**, the survey rendered as lines for
anything that displays free text. LT, so 10240 characters — a survey uses a few hundred:

    Patient: TAB-A3F2-0007, age 42, MALE, MARRIED
    Vitals: Ht 168cm, Wt 61.5kg, BP 130/85 mmHg, HR 78bpm, RR 18/min, Temp 36.8C, SpO2 98%
    History: HYPERTENSION, DIABETES, other Migraine, meds Metformin 500mg
    Symptoms: COUGH_2WEEKS_PLUS, NIGHT_SWEATS
    TB history: diagnosed NO, treated NO, contact YES, when 2025, household DONT_KNOW
    Smoking: FORMER, FIVE_TO_10
    Alcohol: yes, ONE_TO_TWO
    Exposure: dust/smoke/chemical, solid fuels
    Contact: 0917-000-0000, 12 Mabini St, Real, Calamba, Laguna, 4027

A section with no answers is left out rather than printed empty, and a `false` is not
printed at all — an exposure line lists only what the patient reported, so what is on
screen is what was answered.

**Layer 3 — the payload itself, byte for byte**, in a private block:

Under the private creator MDVizio-X already uses for its own AI results,
`MDAI_PRIVATE_CREATOR` in group `1001`, so a study carries one private block rather than
two and your existing reader finds these the same way it finds `(1001,1011)` and
`(1001,1012)`. Elements `01`–`20` in that block are yours; ours start at `30`.

| Element | VR | Content |
|---|---|---|
| `(1001,xx30)` | LO | `medmission-survey/1` — names the payload format |
| `(1001,xx31)` | UT | The survey JSON exactly as the tablet sent it |
| `(1001,xx40)` | SQ | One item per answered field |
| ↳ `(1001,xx41)` | LO | Field name, dotted path — `patient.firstName`, `tbInfo.closeContactActiveTB` |
| ↳ `(1001,xx42)` | LO | Value. Lists are comma-joined; booleans are `true`/`false`; over 64 characters it is clipped, and the JSON above has it whole |

`xx` is the block the private creator landed in, per PS3.5 §7.8.1: find the group `1001`
element whose value is `MDAI_PRIVATE_CREATOR`, take its low byte, and the data elements
are at `(1001,<that byte><30|31|40|41|42>)`. Do not hardcode the block.

Unanswered fields are absent from the sequence rather than present and empty, so its
length is how many questions the patient actually answered.

The JSON value is UTF-8. A reader that has never heard of the private creator sees the
element as unknown bytes, which is harmless — the bytes are still the whole survey.
Section 9 documents the JSON.

**Element numbers `30`, `31` and `40`–`42` need to stay reserved for this.** They were
chosen to clear everything the field studies use; confirm the allocation so a later
MDVizio-X release does not claim them.

### The description fields carry a survey summary

A console that reads the worklist and has no way to call the survey API would otherwise
see nothing a patient answered. The three description fields therefore carry the findings
that change how a screening chest film is read, in one line inside the 64-character DICOM
limit:

    TB scr: cough2w,hemoptysis,nightsweats,prevTB,contact

Included when reported: cough for two weeks or more, blood in sputum, night sweats,
weight loss, fever; and from the TB history, a previous diagnosis or close contact with
an active case — a **`YES` only**, never a "don't know". A survey with none of these
falls back to `Bridge:Mwl:ProcedureDescription`. The summary is a prompt for the
operator, not a substitute for the full survey over REST.

**Patient Name is not always present, Patient ID always is.** Key your side on Patient ID.

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

### MPPS closes studies for you

With the console's MPPS destination pointed at the bridge, nobody has to remember to
clear the worklist:

| Console sends | Performed Procedure Step Status | Record becomes |
|---|---|---|
| N-CREATE at exposure start | `IN PROGRESS` | `InProgress` |
| N-SET at the end | `COMPLETED` | `Completed` |
| N-SET on an abandoned exam | `DISCONTINUED` | `Cancelled` |

The N-CREATE has to name the survey, by Patient ID or by the Study Instance UID the
bridge supplied in the worklist — either identifies it. A step that names neither, **or
that names a patient this bridge never received**, is answered with `No Such Object
Instance` rather than `Success`: acknowledging a step the bridge cannot track would leave
the study open with nothing to show why. The N-SET then needs only the step's own SOP
Instance UID; the bridge remembers the link.

That link is held in memory. A bridge restarted between the start and the end of one
exposure loses it, answers the N-SET with `No Such Object Instance`, and logs it; the
operator closes that single study from the bridge page. Nothing else is affected.

An unrecognised status changes nothing — guessing could close a study that was never
shot — and a status the record cannot legally move to is logged and ignored.

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

Written against MDVizio-X AI 1.0.0 as documented in its user manual. Items 1 and 2 are
the ones that decide the shape of the integration.

1. **MPPS — implemented on our side, needs turning on in the console.** The manual
   documents an MPPS destination under Settings → Network alongside Worklist and PACS.
   Point it at the same host, port and AE title as the worklist (section 6) and the
   console closes its own studies. Nothing else is needed from your software; we would
   like to watch the first one go through together.
2. **Which image does the reading workflow use?** Every answer the patient gave is now
   in the worklist item (section 5), so if the console carries those attributes into the
   image, the survey travels with the study to PACS and to the AI step with nothing to
   fetch. But `ai.dcm` is regenerated by LEADTOOLS and arrives carrying that library's
   sample identity — `John^Doe`, `123-45-6789` — in all 160 field studies we read
   (`docs/field-data-findings.md`). The survey would be lost there along with the patient.
   If the AI-annotated image is what gets read or archived, that has to be fixed first,
   and it is worth fixing regardless of this integration.
3. **Modality — now `DX`.** The field studies are all `DX`, so that is the default. A
   site whose detector reports `CR` changes one setting.
4. **Character set — the one real interoperability risk.** The bridge declares
   `ISO_IR 192` (UTF-8). The field studies are written as `ISO 2022 IR 149`, the Korean
   set, which has no room for the Latin letters Philippine names actually use: a patient
   named Peña or Muñoz is the test case, not a Korean name. Worth one exposure with such
   a name before a site opens.
5. **AE title enforcement.** Any calling AE is accepted today, logged but not checked.
   Say the word and we will restrict it to the console's AE.
6. **Where do the images actually end up?** A PACS server PC travels to sites, but
   network trouble means it often goes unused, and then a day of images — 10.8 GB at 150
   patients — stays on the acquisition laptop. The bridge could take a Storage SCP role so
   images and surveys land in one place that needs no server of its own, which is a real
   scope decision rather than a small one, but it is the same problem the survey side
   already solved by not needing a server.
7. **TLS.** Deferred by agreement on the premise of an isolated field network. If the
   laptop will ever sit on a routed hospital network, this needs revisiting before
   go-live — the API key crosses the wire in plaintext today.
8. **Accession uniqueness.** `no` is assembled on the tablet from a device prefix and a
   counter; two tablets with the same prefix can collide. If your side keys on accession,
   we should tighten the format.
