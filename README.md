# MedMission Bridge

Laptop-side receiver for the MedMission tablet survey app: HTTP ingest,
management worklist UI, and DICOM Modality Worklist (MWL) SCP for the
co-located medical software.

## Run

    dotnet run --project src/MedMissionBridge

- Management UI: http://localhost:18080/ (loopback-only)
- Tablet ingest: POST /api/v1/surveys with `X-Api-Key` (LAN)
- Survey lookup: GET /api/v1/surveys/{recordId} or ?accession= (LAN, keyed)
- MWL SCP: port 11112, AE `MEDMISSION` (C-ECHO supported). The AE title is
  advertised, not enforced — the SCP accepts any calling/called AE. If only
  the co-located medical software queries MWL, set `Mwl:ListenAddress` to
  `127.0.0.1` to take the DICOM port off the LAN.
- mDNS: advertises `_medmission._tcp` for tablet discovery

Configuration: `src/MedMissionBridge/appsettings.json`, section `Bridge`
(API key, ports, AE title, modality, DB path, mDNS name). Data and logs:
`%ProgramData%\MedMissionBridge\`.

## Contracts

- Payload and semantics: tablet repo `docs/reference/wire-contract.md`.
- Design: `docs/superpowers/specs/2026-08-15-medmission-bridge-design.md`.

## Package for deployment

    dotnet publish src/MedMissionBridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish

Produces `publish/` (~106 MB): `MedMissionBridge.exe` plus `appsettings.json`
and `wwwroot/` — copy the whole folder to the field laptop and run the exe;
no .NET installation is required there. Edit `appsettings.json` (API key!)
before the first run. Data and logs land in `%ProgramData%\MedMissionBridge\`.

## Test

    dotnet test
