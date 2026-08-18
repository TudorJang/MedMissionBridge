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
`%ProgramData%\MedMissionBridge\` — the database, `logs\` (daily rolling, 30 kept),
and `backups\` (snapshots taken from the management page, 14 kept).

### Backups

The management page has a **Back up database** button: it runs `VACUUM INTO` and writes a
consistent snapshot to `backups\`, which the operator then copies to external media. The
site's data exists only on that laptop, and file-copying a live SQLite database can yield
an unreadable copy, so this is the supported way to get one. Snapshots are named by
timestamp, the newest 14 are kept, and files the operator put in that folder are never
touched.

### The API key

Leaving `Bridge:ApiKey` at its shipped `changeme-dev-key` placeholder no longer
means running unauthenticated. On first start the bridge generates a key unique to
that laptop, stores it in `%ProgramData%\MedMissionBridge\api-key.txt`, and reuses
it on every later start — tablets configured once keep working across reboots. The
management page shows the running key with a copy button; type it into each tablet.

Setting a real value in `appsettings.json` still wins and skips generation entirely,
which is what you want when one key has to cover several laptops. To rotate a
generated key, delete `api-key.txt` and restart — every tablet then needs the new one.

## Contracts

- Payload and semantics: tablet repo `docs/reference/wire-contract.md`.
- Design: `docs/superpowers/specs/2026-08-15-medmission-bridge-design.md`.
- For the imaging-software team (MWL and REST, hand this one over): `docs/integration-spec.md`.
- For the person running the laptop at a site (print it): `docs/field-operator-guide.md`.

## Package for deployment

    dotnet publish src/MedMissionBridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish

Produces `publish/` (~106 MB): `MedMissionBridge.exe` plus `appsettings.json`
and `wwwroot/` — copy the whole folder to the field laptop and run the exe;
no .NET installation is required there. Set the ports in `appsettings.json` if the
site needs non-default ones; the API key takes care of itself (see above). Data and logs land in `%ProgramData%\MedMissionBridge\`.

### Check the page on each field laptop

Open `http://127.0.0.1:18080/` after the first run. The bridge runs the per-laptop
deployment checks itself and prints what it found above the record list; act on any
line marked ⚠ before the site opens. Nothing else has to be verified by hand.

Two of them are worth knowing in advance, because both are laptop-specific and
neither is the bridge misbehaving:

- **The MWL port failed to bind.** Windows reserves TCP ranges for Hyper-V and
  WinNAT, and the default port 11112 falls inside one on some machines. The bridge
  reads the reservations (`netsh int ipv4 show excludedportrange protocol=tcp`),
  says which range is in the way, and names a free port to switch to — set
  `Bridge:Mwl:Port` to it, restart, and tell the X-ray software the new port.
  Survey ingest keeps working throughout; only the worklist is down.
- **The advertised address may not be the field LAN one.** The bridge advertises
  only NICs that look like the real LAN, but an unusual VPN or virtual adapter can
  still fool the detection, and only a person standing next to both machines can
  confirm the address. The page always states the address tablets are given; if it
  is wrong, pin `Bridge:Mdns:AdvertiseAddress`. Tablets can also be given the
  address by hand.

## Test

    dotnet test
