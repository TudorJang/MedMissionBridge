# MedMission Bridge — Design

The laptop-side companion to the MedMission tablet survey app
(`D:\MedMissionSurveyApp`): it receives survey payloads over the wireless
LAN, gives the field team a management worklist, and answers standard DICOM
Modality Worklist (MWL) queries from the medical software on the same laptop.

## 1. Background

The tablet app is complete and pushes each survey as JSON to
`POST /api/v1/surveys` on a laptop it discovers via mDNS. The receiving side
did not exist until now. The laptop also runs a separate medical software
suite (C# and JavaScript, including multiple ONNX classification models for
chest X-ray AI assessment) which consumes patient data **via DICOM MWL** and
already owns the logic that embeds survey data into its DICOM outputs as
custom tags. The bridge's job is to connect these two worlds without
requiring changes to either: the tablet keeps its existing wire contract,
and the medical software keeps its standard MWL client plus its existing
custom-tag pipeline — it only gains a data source.

Authoritative upstream contracts (in the tablet repo, binding on this
project):

- `docs/reference/wire-contract.md` — the exact JSON the tablet POSTs,
  serialization rules (absent-not-null), idempotency requirement, date
  format (ISO-8601), and the narrowed meaning of `patient.address`.
- `NsdDiscoveryService.kt` — the tablet discovers laptops by browsing mDNS
  service type `_medmission._tcp` and uses the advertised service name,
  host and port as-is. Manual host:port entry also exists as a fallback.

## 2. Environment and constraints

- Windows 11 laptop on a wireless LAN (field deployment, assume no
  internet).
- Coexists on the same machine with the C#/JS medical software; the bridge
  must not assume it is the only busy process (ONNX inference runs there).
- Multiple tablets may send to one laptop; multiple laptops may exist on
  the network (each advertises itself; tablets pick one).
- Transport is HTTP with the pre-shared `X-Api-Key`, per the current wire
  contract. TLS is deliberately deferred (see §10).

## 3. Architecture

One .NET 8 process (`MedMissionBridge.exe`), four units with narrow
interfaces:

```
MedMissionBridge.exe (.NET 8)
├─ Ingest API      ASP.NET Core (Kestrel): POST /api/v1/surveys + survey
│                  lookup GETs. Bound to all interfaces (LAN-facing).
├─ Worklist Store  SQLite (single file) via EF Core. Upsert on recordId.
├─ MWL SCP         fo-dicom 5.x: C-FIND responder for Modality Worklist.
│                  Port and AE Title configurable.
├─ mDNS Advertiser publishes `_medmission._tcp` with the Ingest API port.
└─ Web UI          static HTML/JS served by the same Kestrel, plus
                   /api/ui/* endpoints. Loopback-only (see §6).
```

Unit boundaries: the MWL SCP and the Web UI both depend only on the store's
query/update interface; neither knows HTTP ingestion exists. The mDNS
advertiser knows only the configured service name and the ingest port.

Solution layout:

```
MedMissionBridge.sln
├─ src/MedMissionBridge/         the application
└─ tests/MedMissionBridge.Tests/ xUnit tests
```

Key dependencies: ASP.NET Core (in .NET 8), `fo-dicom` (5.x),
`Microsoft.EntityFrameworkCore.Sqlite`, an mDNS advertiser library
(`Makaretu.Dns.Multicast` or equivalent — the implementation plan verifies
the choice against Windows 11 behavior), `Serilog` (rolling file logs),
`xUnit`.

## 4. Tablet-facing contract (ingest)

`POST /api/v1/surveys`

- Validates `X-Api-Key` against the configured key; mismatch → `401`.
- Parses the body per the wire contract. The tablet never blocks on
  missing fields, so the bridge accepts any payload with a `recordId`;
  everything else is optional. A body with no parseable `recordId` → `400`
  (a real tablet never sends this).
- **Upserts by `recordId`** — an insert and a re-send/edit-re-send are the
  same operation. Returns `200` with an empty body. Any storage failure →
  `500`, which the tablet treats as retry-later. This makes the endpoint
  idempotent, matching the tablet's retry loop (up to 10 attempts) and
  edit-then-resend behavior.
- The **raw JSON body is stored verbatim** alongside extracted columns.
  Extraction covers what the worklist, MWL and UI need (names, birth date,
  gender, `no`, `date`, address fields); anything else stays available in
  the raw payload without schema churn.

mDNS: the bridge advertises `_medmission._tcp` with a configurable service
name (default: the machine name) and the ingest port. Tablets then list the
laptop automatically; manual host:port entry keeps working regardless.

## 5. Storage and worklist lifecycle

SQLite, one file at `%ProgramData%\MedMissionBridge\bridge.db` (created on
first run). EF Core with migrations.

`SurveyRecord` table (columns beyond bookkeeping):

| Column | Source | Notes |
|---|---|---|
| `recordId` (PK) | payload | UUID string |
| `receivedAtUtc`, `updatedAtUtc` | bridge | audit |
| `status` | bridge | see lifecycle below |
| `no` | payload | accession number for DICOM |
| `date`, `firstName`, `lastName`, `birthDate`, `gender` | payload | worklist + MWL |
| `region`, `province`, `city`, `barangay`, `zip`, `address` | payload | UI detail/search |
| `rawJson` | payload | complete original body |

Lifecycle: `RECEIVED → IN_PROGRESS → COMPLETED`, with `CANCELLED` reachable
from `RECEIVED`/`IN_PROGRESS`. Status changes happen only in the bridge UI
(v1 has no MPPS). A re-sent payload updates the survey columns and
`rawJson` but **preserves the current status** — an edit from the tablet
must not silently resurrect a completed study onto the modality worklist.

## 6. Web UI (management)

Served by the same Kestrel instance. **UI pages and `/api/ui/*` endpoints
respond only to loopback requests** (middleware check): the operator uses
the laptop's own browser at `http://localhost:<port>/`, and the LAN sees
only the ingest/lookup API. This keeps the management surface off the
wireless network without adding a login system.

Capabilities (all v1):

- **List** with newest-first ordering, **status filter**, and **search**
  over name, `no` and city.
- **Detail view** of a record: all survey sections, rendered from
  `rawJson` (labels mirror the tablet form's sections).
- **Status changes** with the transitions from §5.
- A small header showing bridge health: ingest port, MWL port/AE Title,
  mDNS state, database path.

Implementation: static HTML + vanilla JS (fetch), no build step, no
framework. The UI is a thin client over `/api/ui/records` (list/search),
`/api/ui/records/{recordId}` (detail = raw JSON + status), and
`POST /api/ui/records/{recordId}/status`.

## 7. DICOM MWL SCP

fo-dicom `DicomService` implementing C-FIND for the Modality Worklist
Information Model (1.2.840.10008.5.1.4.31). Defaults: port `11112`
(avoids the sub-1024 admin requirement of 104; configurable), AE Title
`MEDMISSION` (configurable). The medical software points its worklist
client at `<laptop>:<port>` / AE Title — configuration on its side, no
code changes.

**Only `RECEIVED` and `IN_PROGRESS` records appear in MWL results.**
Completing or cancelling a study in the UI removes it from the worklist,
per the standard convention.

Attribute mapping (survey → MWL response dataset):

| MWL attribute | VR | Source | Rule |
|---|---|---|---|
| Patient's Name (0010,0010) | PN | `lastName`, `firstName` | `Last^First`; empty components omitted |
| Patient ID (0010,0020) | LO | `recordId` | UUID string (≤64 chars, fits LO) |
| Patient's Birth Date (0010,0030) | DA | `birthDate` | ISO parsed → `YYYYMMDD`; unparseable/partial → attribute absent (wire contract §6 parse-or-ignore) |
| Patient's Sex (0010,0040) | CS | `gender` | `MALE→M`, `FEMALE→F`, absent otherwise |
| Accession Number (0008,0050) | SH | `no` | e.g. `TAB-3FBB-0001` (13 chars, fits SH) |
| Sched. Procedure Step Date (0040,0002) | DA | `date` | ISO → `YYYYMMDD`; unparseable → today |
| Sched. Procedure Step Time (0040,0003) | TM | bridge | `receivedAtUtc` local time |
| Modality (0008,0060) | CS | config | default `CR`, configurable (`DX` etc.) |
| Sched. Station AE Title (0040,0001) | AE | config | the bridge's configured target station AE, default same as bridge AE |
| Sched. Procedure Step Description (0040,0007) | LO | fixed | `"TB Screening Chest X-Ray"` (configurable) |

The Scheduled Procedure Step Sequence (0040,0100) wraps the scheduled-step
attributes per the MWL information model. C-FIND matching supports the
customary keys: Patient Name (wildcard), Patient ID (single value),
Scheduled Procedure Step Date (single value and range), Modality. Unmatched
optional return keys are returned empty rather than rejected.

## 8. Survey handoff to the medical software

The medical software keeps its existing custom-tag pipeline; the bridge is
its data source. Two channels were considered; **only the first is built**.

**REST lookup (v1).** After the medical software receives a worklist item,
it fetches the full survey with one localhost HTTP call, using either key
it has from MWL:

```
GET /api/v1/surveys/{recordId}            (recordId = MWL Patient ID)
GET /api/v1/surveys?accession={no}        (no = MWL Accession Number)
```

Both require `X-Api-Key` (same key as ingest) and return the stored
`rawJson` — byte-identical to what the tablet sent, so
`wire-contract.md` is the single parsing contract for both the bridge and
the medical software. `404` when unknown; the accession form returns the
most recently updated match if tablets ever collide on `no`.

**MWL private tag (reserved, not implemented).** If the medical software
team later determines that receiving the survey inside the MWL response
itself saves them work, the pre-agreed layout is: private creator
`MEDMISSION` at `(7777,0010)`, survey JSON (UTF-8) as UT at
`(7777,1001)`, enabled by a bridge config flag (`Mwl:IncludeSurveyJson`,
default off). Reserving the layout now means adding it later is a small
change on both sides with no contract negotiation. Nothing in v1
implements, tests or documents this beyond this paragraph.

## 9. Reliability, errors, logging

- Ingest is idempotent (§4); the tablet's retry loop is the recovery
  mechanism for transient failures. The bridge never needs to dedupe.
- The MWL SCP returns standard C-FIND status codes; a store failure during
  a query yields "Unable to process" rather than a hung association.
- All units log through Serilog to a rolling file under
  `%ProgramData%\MedMissionBridge\logs\`, with received `recordId`s, MWL
  association peers, and status transitions — enough to reconstruct a field
  day's traffic after the fact.
- The process is started by the operator (desktop shortcut). If it isn't
  running, tablets simply keep retrying and the mDNS entry is absent —
  consistent with how the tablet already behaves toward an offline laptop.

## 10. Security

- Closed field network assumption: transport stays HTTP with the pre-shared
  `X-Api-Key`, matching the current tablet contract. This is a known,
  accepted risk for the pilot — patient data crosses the WLAN unencrypted.
- The management UI and its APIs are loopback-only (§6), so the LAN surface
  is exactly: ingest POST, the two lookup GETs (all key-gated), and the
  DICOM port.
- TLS is a configuration switch away on Kestrel but **off in v1** because
  enabling it requires a coordinated tablet change (certificate trust).
  The decision to flip both sides belongs to the production-deployment
  checklist, not this project.

## 11. Testing strategy

TDD throughout (project convention). xUnit.

- **Unit**: payload→entity extraction (absent-not-null handling), DICOM
  conversions (ISO→DA including partial/invalid dates, PN composition,
  sex mapping), status transition rules (including re-send preserving
  status), MWL matching predicates.
- **Integration**: ASP.NET `WebApplicationFactory` round-trips — ingest
  then lookup returns byte-identical JSON; upsert idempotency; 401/400
  paths; loopback-only enforcement of `/api/ui/*`. MWL: fo-dicom *client*
  against the in-process SCP — a real C-FIND association asserting the
  §7 mapping table and the status-visibility rule.
- mDNS advertising is verified manually on the target laptop (Windows
  mDNS behavior varies too much for CI to be honest about it), plus a
  live end-to-end check with a real tablet: discover → send → appears in
  UI → visible via C-FIND.

## 12. Out of scope (v1)

TLS enablement, MPPS (automatic performed-step status from the modality),
receiving AI results back from the medical software, PDF/report
generation, user accounts or roles, multi-laptop synchronization, and the
MWL private-tag channel (§8). Interfaces are shaped so none of these are
foreclosed.

## 13. Open items

- The medical software's MWL client conformance (which matching keys it
  actually sends, expected AE Title/port) — absorbed by bridge
  configuration when known; no design impact expected.
- The mDNS library choice is verified early in implementation (Task-level
  spike) since Windows mDNS interop is the one dependency this design
  takes on faith.
