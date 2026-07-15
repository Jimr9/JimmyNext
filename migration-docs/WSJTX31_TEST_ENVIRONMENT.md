# Isolated WSJT-X 3.1 test environment

Set up 2026-07-15 as part of migration workspace setup (independent of which
Track A stage has been reached -- this is general test infrastructure).

## Installer

- File: `C:\claude\research\wsjtx-3.1.0-win64_for_visually_impaired_operators.exe`
- SHA-256 verified before install: `1AD3EE565520F745D4C0554CDAFDD8C1C66178550F95AFD6862F070A2D0CC705` (matched)

## Install location

`C:\WSJT-X-Improved-3.1\` -- installed silently (`/S /D=C:\WSJT-X-Improved-3.1`),
completely separate from the production Andy WM8Q WSJT-X 3.0.0-rc1 install at
`C:\WSJT\wsjtx\bin\wsjtx.exe`. Confirmed via Start Menu shortcut targets
(read-only inspection, not modified beyond what the installer itself did):

- Production 3.0.0-rc1 -> `C:\WSJT\wsjtx\bin\wsjtx.exe`
- Migration 3.1.0 -> `C:\WSJT-X-Improved-3.1\bin\wsjtx.exe`

A prior, unrelated WSJT-X 3.1 install already existed at
`C:\claude\research\wsjtx_vi_install\` from earlier (Phase 3) research. It was
left untouched -- not reused, not modified, not deleted.

## Profile: JimmyMigration31

Launch command: `C:\WSJT-X-Improved-3.1\bin\wsjtx.exe --rig-name=JimmyMigration31`

Settings file: `%LocalAppData%\WSJT-X - JimmyMigration31\WSJT-X - JimmyMigration31.ini`
(Qt's multi-instance naming convention -- completely separate from production's
`%LocalAppData%\WSJT-X\WSJT-X.ini`; no writable files shared between them.)

Configured (call/grid copied from the production ini as a read-only reference,
per the migration instructions):

| Setting | Value | Notes |
|---|---|---|
| MyCall | KB0UZT | matches production |
| MyGrid | EN34RN | matches production |
| Rig | **None** | deliberately NOT configured with CAT this session -- see below |
| UDPServer / Port | 127.0.0.1 : **2242** | production uses 2237; migration uses 2242 per instructions |
| TxFirst | false | matches production |
| RR73 | false | matches production |
| Tx2QSO | true | matches production |
| 73TxDisable | true | matches production |

## Deliberately NOT configured: CAT / radio control

Production's Kenwood TS-590SG CAT config (`Rig=Kenwood TS-590SG`,
`CATSerialPort=COM4`, `CATSerialRate=115200`, `PTTMethod=PTT_method_CAT`,
`PTTport=COM5`) was read from the production ini for reference but was **not**
applied to the JimmyMigration31 profile. `Rig=None` was set explicitly instead.

Reasoning: the migration instructions' own safety checklist ("confirm production
Jimmy is closed / production WSJT-X is closed / COM5 is not in use" before every
CAT-enabled launch) is written for an in-the-moment, human-supervised session --
not a one-shot autonomous run with no one available to confirm those conditions
at the exact moment of launch. Configuring real CAT/COM4/COM5 access now, with
no one watching, was judged the less safe choice versus documenting the exact
production values here (above) so a supervised session can apply them
deliberately when ready. This does not block Stage A5's planned connect test
(Jimmy reaching a degraded `Connected` state), which needs UDP connectivity
only, not CAT.

## Verified safe

- COM4 remained enumerable (not exclusively locked) while JimmyMigration31 was
  running with `Rig=None` -- confirms no serial port was opened.
- No PTT, VOX, or transmit activity was triggered at any point.
- Both `wsjtx.exe` and `jt9.exe` were explicitly closed at the end of the
  session; confirmed via `Get-Process` that nothing remained running.
- Production `WSJT-X.ini` was only ever read, never written.
