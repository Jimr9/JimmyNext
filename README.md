# Jimmy Next

Jimmy Next is a Windows application that makes FT8 and FT4 operation accessible to blind
amateur radio operators, with a keyboard-driven interface built for screen readers (JAWS and
NVDA). It is the next generation of **Jimmy**, KB0UZT's accessible FT8/FT4 companion for
WSJT-X — currently in public beta.

**Jimmy Next bundles its own FT8/FT4 decode/transmit engine.** Unlike production Jimmy, it does
not require WSJT-X or any modified WSJT-X build at all — Jimmy Next talks directly to a
bundled engine host (built on the third-party [Nexus](https://github.com/kd9taw/Nexus) engine,
GPL-3.0) over a local control connection. Install Jimmy Next and go; there is nothing else to
install alongside it.

## Key Features

- **Keyboard operation, accessible with screen readers** — actions are reachable without a mouse, with concise speech output.
- **Accessible FT8/FT4 station selection and reply workflow** — select and respond to stations from the keyboard.
- **Call queue management** — includes filtering and prioritization options for incoming calls.
- **Award tracking with live "still needed" indicators** — supports common award programs, highlighting relevant stations as they're heard.
- **Logbook and lookup features** — includes local logging, callsign lookup, and related operating tools.
- **Integrates with QRZ, Club Log, LoTW, eQSL, and PSK Reporter** — supports integration with these services for logging, confirmations, and spotting.
- **Appearance and display options** — offers alternate color themes and an advanced display layout for additional detail.

## Project Status

Jimmy Next is a **public beta**. It is under active development and testers should expect
occasional rough edges — please report anything that looks wrong. Features, award definitions,
and behavior may change as the project evolves. Production Jimmy (the WSJT-X-companion version)
remains the stable release for operators who want to stay off the beta channel; Jimmy Next and
production Jimmy install and update completely independently of one another.

## Background

Jimmy began as **Tilly**, created by Andy WM8Q. Tilly provided the foundation for accessible
FT8/FT4 operation with keyboard control, audio feedback, queue handling, and WSJT-X UDP
integration. Jimmy is a modified and expanded continuation of that work. Jimmy Next carries that
same operator-facing experience forward onto its own bundled decode/transmit engine, removing the
external WSJT-X dependency entirely.

## Requirements

- Windows 10 or 11 (64-bit)
- No separate .NET install required — Jimmy Next ships self-contained.
- No WSJT-X install required — Jimmy Next's engine is bundled.

## Getting Started

1. Install the latest `JimmyNext.msi` from [Releases](https://github.com/jimr9/JimmyNext/releases/latest) (see [Installation](#installation) below).
2. Start Jimmy Next. It launches its own bundled engine automatically — there is no separate program to start first.

## Installation

Download the latest `JimmyNext.msi` (or `JimmyNext.msi.zip`) from the
[Releases](https://github.com/jimr9/JimmyNext/releases/latest) page and run it. Jimmy Next
installs side by side with production Jimmy if both are present — it uses its own install
folder, its own settings/data location, and its own update channel, and will never upgrade,
replace, or remove a production Jimmy install (or vice versa).

## Building from Source

Open `WSJTX_Controller\Jimmy.csproj` and build in Release mode (net10.0-windows), or run
`build.bat` for a local Debug build. See `ARCHITECTURE.md` for how the C# UI, the bundled Rust
EngineHost, and Nexus fit together.

To build the installer: `wix build -o Release\JimmyNext.msi JimmyNext.wxs` (from `Setup_WiX\`)

## More Information

- Discussion group: https://groups.io/g/tilly-beta/topics

## Acknowledgements

Jimmy is built on **Tilly**, the original accessible WSJT-X companion application created by
**Andy WM8Q**. Andy's work on Tilly — including the UDP integration, audio feedback system,
keyboard control model, and queue handling — made this project possible. Jimmy Next's bundled
engine is built on **Nexus** by kd9taw. Thank you both.

## License

Jimmy is based on Tilly, which was released under GPL-3.0. See the [LICENSE](LICENSE) file for
details.
