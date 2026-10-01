# Jimmy Next 2.0.81 — release notes

- **Send a support report straight to KB0UZT.** Create Support Report (in Help) has a new
  **How to send it** choice: **Send to KB0UZT** (the default) or **Save to my computer only**.
  Send copies the report's location and opens a private upload page: choose Add files, press
  Control+V to paste, press Enter, then Upload. Only KB0UZT can see what is uploaded.
- **You choose where the report is saved.** A Save box opens with Downloads offered; press Enter
  to keep it or pick any folder.

# Jimmy Next 2.0.80 — release notes

A fix release for 2.0.79.

- **The logbook move works for older logbooks.** If you had not run Jimmy Next for a while
  before 2.0.79, your logbook was in an older format and the move to Nexus stopped at every
  start ("no such table: qso_extra_field"). It now moves normally the next time you start.
  Your old logbook was never changed, and it is backed up again before the move.
- **Stations are no longer all hidden while the logbook is not ready.** While the logbook is
  still loading, or if its move did not finish, Jimmy Next cannot tell who you have worked.
  2.0.79 treated every station as "already worked", so the call lists stayed empty. Stations
  now appear as usual; only the new-station alerts wait for the logbook.
- **Support reports include everything needed to help you:** your whole Jimmy Next folder
  (settings, logs, logbook move reports), with every password and login removed. Downloaded
  lookup data is left out. A new **Include my logbook** check box (on by default) lets you
  leave your contacts out.

# Jimmy Next 2.0.79 — release notes

## Your logbook now lives in Nexus

- The first time you start this version, your existing log is moved into the Nexus engine
  automatically. The old logbook is backed up first. A contact made while the move is running
  waits in line and is added when it finishes.
- **There is no going back.** The old built-in logbook and the command to move back to it have
  been removed. Screens, awards and uploads work as before.
- The contact editor (Logbook, Enter or Space on a contact) shows every field. Protected fields
  need "Allow editing protected fields"; confirmations (LoTW, QRZ, eQSL, card) are always read only.

## Setup first

- **First start opens setup:** Station (callsign, grid), Radio (model, COM port, PTT, SWR halt),
  Audio (the radio's input and output devices), then Operating (Call CQ or Listen, CQ /
  CQ DX, who to reply to, POTA role, reply order), with Back / Next / Finish. It opens again
  at each start until everything is set, and until then the status line says exactly what is
  missing and on which Options page. The old Options "Basic" page is now that last step.
- **No sound card is touched until the radio is set up** -- not even Windows' default device.
  Until then the logbook, imports, uploads and lookups still work (once the callsign and grid
  are in). "Receive Only" and "System default" audio are gone: Jimmy Next always talks to the
  radio through Hamlib rigctld and the radio's own audio devices.

- **Lookup data is on from the first start** on a new install: the FCC license database
  (about 170 MB, downloaded in the background, refreshed every 7 days) and the LoTW users list
  download on their own, and the Club Log country data now refreshes every 7 days. QRZ stays
  off until you enter a login. Settings you have already saved are kept.

## Profiles

- **Shared settings:** callsign and grid, operator name and contest email, TQSL station location,
  and the QRZ, LoTW, Club Log, HRDLog, eQSL and HamQTH logins are shared by every profile.
  Check **This profile only** beside any of them to give one profile its own value. The first
  start moves your profiles over and backs them up; a profile with different values keeps them.
- Switching profiles no longer restarts the engine when only the radio, audio or internet time
  check differ. A crash when switching profiles is fixed.
- "Save current configuration first" keeps the choice you made.
- **Export and Import Customizations** (Options, Profiles): share your notifications and sounds
  -- and, if you choose, calls and operating (wanted calls, Spot Watch, ranking, auto-reply,
  Smart Mode), hotkeys, list display, the band frequency table and contest categories --
  with another operator as one file. The
  sound files themselves go inside it. Radio, audio, station (including a contest's section), logins and
  windows are never included. Import
  asks what to bring in, backs up your settings first (a checkbox, on by default), and reloads.

## Smart Mode

- **Smart QSO Start is now called Smart Mode.** Your setting is kept.
- **With Smart Mode off, Jimmy Next keeps calling a station until the repeat limit, whether or
  not it is busy with someone else** — as before Smart Start existed. Previously it also stopped
  calling a busy station with the option off. With Smart Mode on, nothing changes: it waits
  while the station is busy and yields after "Other-station replies before yielding".
- **Limits and counters in your notifications** (Options, Notifications): every notification and
  status template can say {RepeatLimit} and {RepeatCount}, {SilencePeriods} and {SilenceCount},
  {NotHeardLimit} and {NotHeardCount}, {TimeLimit} and {TimeElapsed} (minutes), and
  {RepliesLimit} and {RepliesCount} -- each setting, and where it stands right now.

## Sounds and alerts

- The receive summary now says new DXCC and new DXCC on band apart: "1 new DXCC, 1 new DXCC on
  band" (each counts stations).
- **Your own sounds folder:** put your own .wav files in %LOCALAPPDATA%\Jimmy Next\Sounds (no
  administrator rights needed). Jimmy Next looks there first, so a file with the same name as a
  built-in sound replaces it, and callsign or award sound files (e.g. W1AW.wav, WAS.wav) work from
  there. Choosing a sound in Options, Sounds opens that folder once it has a sound in it.
- New sounds: **New grid** and **New grid on band** (Options, Sounds).
- **One new DXCC / new grid sound per receive period** (Options, Sounds, off by default): a busy
  period with several new stations plays each of those sounds once instead of once per station.
- **Alert regions** (Options, Sounds, Choose regions): keep new DXCC, new grid, directed CQ,
  POTA, SOTA and call-added sounds to the continents and countries you chase. Default is All
  regions. Calling you and wanted calls always sound.

## Radio

- **Logged power** is the radio's power setting, read only. Kenwood radios are read with their
  own power command; Jimmy Next never uses the Hamlib power read that drops a TS-590 to 5 W.
  Power is left blank until the current radio has been read, and cleared when you switch
  profile or radio.
- **Antenna tuner** (Alt+Shift+T, Kenwood): the radio runs the tune-up and ends it itself;
  you hear "Tuner finished" (the radio beeps if it found no match). If your radio is set to stay
  in transmit after tuning, you hear "Tuner finished, radio still transmitting", and 30 seconds
  later — or at once on Escape or Alt+Shift+T — Jimmy Next returns it to receive and tells you
  whether that worked. Jimmy Next never changes power or radio menus.
- **Internet time check** now changes without restarting the engine.
- **Logged start time (TIME_ON)** is now when the contact began -- when Jimmy Next started
  working the station -- as WSJT-X logs it. It used to be the time of the station's report,
  up to a minute late. Older contacts are unchanged.
- Contacts with a POTA activator log the park and its state; when the state isn't certain it is
  left blank rather than guessed.
  If the activator called CQ POTA but no spot names its park, the contact is logged as POTA
  with the park blank, for you to fill in -- never a guessed park.

## Finishing a contact

- When a station misses your RR73 and repeats its report, Jimmy Next answers each repeat with
  one more RR73, the way WSJT-X and Nexus do, and no longer stops transmitting with "no contact
  in progress". That station is no longer listed again as a new caller. Likewise, when a station
  misses your 73 and repeats its RR73, the one extra 73 sent in answer is treated as part of the
  contact.

## Mouse

- In the advanced layout, double-click a station in the TX1/TX2 or Raw Decodes list to call it
  (same as Enter), and right-click a station in TX1/TX2 to remove it (same as Delete) -- as the
  main call list already did. In the Logbook Edit tab, double-click a contact to edit it.

## Known limitations

- The antenna tuner key works only with Kenwood radios, and has been confirmed on the air only
  on a TS-590SG.
- Power on non-Kenwood radios uses Hamlib's read and has not yet been tried on the air.
