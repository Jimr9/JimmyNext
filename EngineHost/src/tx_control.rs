//! Transmit-control receive path: a dedicated background thread that does nothing but block on
//! `recv_from` and forward recognized commands, so a HaltTx reacts as fast as the OS can deliver
//! the datagram -- never delayed behind the main loop's audio-capture/decode work.
//!
//! Stage 1 (this file, current state): parses Jimmy's outbound Reply/HaltTx and reports what it
//! understood. Does NOT touch audio playback or PTT -- see main.rs's own header comment for the
//! staged build-out this is part of. Bench-testable in complete isolation from real transmit.
//!
//! Jimmy's EnableTxMessage (WM8Q-only, type 16/17 -- see that file's own header comment) is
//! deliberately NOT handled here. It isn't part of real WSJT-X's protocol at all; Jimmy only
//! sends it to a backend CapabilityNegotiator has confirmed speaks the WM8Q Compatibility Layer.
//! This engine host implements none of those extension sub-commands, so CapabilityNegotiator
//! correctly reports it as standard-only, and Jimmy falls back to the same Reply/HaltTx-only
//! behavior it already uses against real, unmodified WSJT-X -- a proven, tested code path this
//! reuses rather than a new one. tempo_net::wsjtx::Inbound (a real-protocol-only parser) has no
//! EnableTx variant either, which is the same conclusion from the other direction.

use std::net::UdpSocket;
use std::sync::mpsc::Sender;

use tempo_core::message::Msg;
use tempo_net::wsjtx::{parse_inbound, Inbound};

#[derive(Debug)]
pub enum Command {
    /// The operator (via Jimmy's queue) wants to work this station -- WSJT-X's own "double-click
    /// a decode" semantics, which `tempo_core::qso::Station::start` already implements exactly
    /// (see qso.rs's own doc comment on that function).
    Reply { dxcall: String, msg: Msg, snr: i32 },
    /// Stop transmitting. `auto_only`: true = only turn off auto-TX, finish the current
    /// transmission; false = stop now. Both must be reacted to immediately once real transmit
    /// exists -- this is the single most safety-critical message this process receives.
    HaltTx { auto_only: bool },
}

/// Spawns the dedicated receive thread on a clone of `sock` (same local port, safe to use
/// concurrently with the main thread's own sends on the original handle) and runs for the life
/// of the process.
pub fn spawn_receiver(sock: &UdpSocket, tx: Sender<Command>) -> std::io::Result<()> {
    let recv_sock = sock.try_clone()?;
    std::thread::spawn(move || {
        let mut buf = [0u8; 2048];
        loop {
            match recv_sock.recv_from(&mut buf) {
                Ok((len, _from)) => handle_datagram(&buf[..len], &tx),
                Err(e) => {
                    // On Windows, a UDP send to a port nobody's listening on (e.g. Jimmy not up
                    // yet, or not up anymore) comes back as an ICMP Port Unreachable, which the
                    // OS surfaces as WSAECONNRESET on this socket's NEXT recv -- routine, not a
                    // real fault, and NOT rate-limited by anything: without a delay here, a
                    // sustained "nobody's listening" condition (e.g. run standalone with no
                    // Jimmy at all) would spin this thread at full CPU logging one line per
                    // iteration forever.
                    eprintln!("jimmy-engine-host: control-socket recv error: {e} (retrying)");
                    std::thread::sleep(std::time::Duration::from_millis(200));
                }
            }
        }
    });
    Ok(())
}

fn handle_datagram(bytes: &[u8], tx: &Sender<Command>) {
    let Some(inbound) = parse_inbound(bytes) else {
        return; // not a recognized WSJT-X datagram -- ignore (matches parse_inbound's own contract)
    };
    match inbound {
        Inbound::Reply { message, snr, .. } => {
            let msg = Msg::parse(&message);
            match msg_sender(&msg) {
                Some(dxcall) => {
                    let _ = tx.send(Command::Reply { dxcall: dxcall.to_string(), msg, snr });
                }
                None => {
                    eprintln!(
                        "jimmy-engine-host: Reply to unparseable/free-text message '{message}', ignoring"
                    );
                }
            }
        }
        Inbound::HaltTx { auto_only, .. } => {
            let _ = tx.send(Command::HaltTx { auto_only });
        }
        _ => {} // Clear/Replay/Location/FreeText/etc: not transmit-control, not handled yet.
    }
}

/// The sender ("de") callsign of a parsed standard message, regardless of which form it is.
/// `None` for `Msg::Other` (free text) -- there is no station to reply to.
fn msg_sender(m: &Msg) -> Option<&str> {
    match m {
        Msg::Cq { de, .. } => Some(de),
        Msg::Grid { de, .. } => Some(de),
        Msg::Report { de, .. } => Some(de),
        Msg::RReport { de, .. } => Some(de),
        Msg::Rr73 { de, .. } => Some(de),
        Msg::Rrr { de, .. } => Some(de),
        Msg::Bye73 { de, .. } => Some(de),
        Msg::FieldDay { de, .. } => Some(de),
        Msg::Other(_) => None,
    }
}
