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

### Check these two on each field laptop

Open `http://127.0.0.1:18080/` after the first run and read the health panel.

- **`mwlRunning` false.** Windows reserves TCP ranges for Hyper-V and WinNAT, and
  the default MWL port 11112 falls inside one on some machines — the DICOM server
  then fails to bind with a socket access error while ingest keeps working. Run
  `netsh int ipv4 show excludedportrange protocol=tcp`; if 11112 is listed, set
  `Bridge.Mwl.Port` to a free port (12112 works) and tell the X-ray software the
  new port.
- **`mdnsAddresses` shows an address tablets cannot reach.** The bridge advertises
  only NICs that look like the real LAN, but a laptop with an unusual VPN or
  virtual adapter can still fool the detection. Whatever is listed here is exactly
  what tablets are told to connect to, so if it is not this laptop's LAN address,
  pin it with `Bridge.Mdns.AdvertiseAddress`. Tablets can always fall back to
  entering the address by hand.

## Test

    dotnet test
