//! WSJT-X-format output to other programs, receive-only (2026-10-12).
//!
//! Nexus's radio loop sends what WSJT-X sends -- Heartbeat, Status, Decode, Clear, and for each
//! logged contact QSOLogged then LoggedADIF -- to the address in `Settings.wsjtx_udp_addr`, and
//! it also OBEYS what comes back on that same socket: a `Reply` datagram starts a call and turns
//! transmit on, `FreeText` transmits text, `HaltTx` stops transmit. That is right for Nexus's
//! own desktop, and wrong for Jimmy Next: a logging or mapping program beside Jimmy is there to
//! watch and to log, and must never be able to key the operator's radio.
//!
//! So Nexus is pointed at a private loopback port that only this relay reads, and the relay
//! sends each datagram on, unchanged, from a socket of its own to the operator's chosen
//! destinations. Other programs therefore only ever see the relay's socket. Whatever they send
//! back arrives here, is counted, and is DROPPED -- it never reaches the socket Nexus listens
//! on. No Nexus source is changed.
//!
//! What a program beside Jimmy gets: decodes, status, and each contact as it is logged.
//! What it does not get: any way to reply, call, send free text, halt, move the radio or ask
//! for a replay of the current period (it sees the next period's decodes instead).
//!
//! "Sent" means handed to the network. UDP gives no receipt, so nothing here ever says a
//! program received anything; the status reports only what was sent and whether anything at
//! all has come back from each destination.

use std::net::{SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Destinations as the operator wrote them: one or more `address:port`, separated by commas,
/// semicolons or spaces. Returns the valid ones and, for each entry that is not, why.
pub fn parse_targets(text: &str) -> (Vec<SocketAddr>, Vec<String>) {
    let mut targets = Vec::new();
    let mut problems = Vec::new();
    for entry in text.split([',', ';', ' ', '\t', '\n']).map(str::trim).filter(|s| !s.is_empty()) {
        match entry.parse::<SocketAddr>() {
            Ok(a) if a.port() == 0 => problems.push(format!("{entry}: port 0 is not a port")),
            Ok(a) if a.ip().is_unspecified() => problems.push(format!("{entry}: not an address a program listens on")),
            Ok(a) => {
                if !targets.contains(&a) {
                    targets.push(a);
                }
            }
            Err(_) => problems.push(format!("{entry}: not address:port, such as 127.0.0.1:2237")),
        }
    }
    (targets, problems)
}

#[derive(Default)]
struct Counters {
    forwarded: AtomicU64,
    send_errors: AtomicU64,
    ignored_inbound: AtomicU64,
}

/// One destination's last word to us, if any (Unix seconds).
struct Heard {
    addr: SocketAddr,
    last_unix: AtomicU64,
}

pub struct Relay {
    /// Where Nexus is told to send (`Settings.wsjtx_udp_addr`): loopback, this relay only.
    pub nexus_addr: SocketAddr,
    targets: Vec<Arc<Heard>>,
    counters: Arc<Counters>,
    stop: Arc<AtomicBool>,
    last_error: Arc<Mutex<Option<String>>>,
}

fn now_unix() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

impl Relay {
    /// Starts the relay for `targets`. `Err` = it could not be set up; the caller then leaves
    /// WSJT-X output OFF rather than pointing Nexus straight at another program.
    pub fn start(targets: Vec<SocketAddr>) -> Result<Relay, String> {
        if targets.is_empty() {
            return Err("no destination".to_string());
        }
        // From Nexus: loopback only, an ephemeral port nobody else is told.
        let from_nexus = UdpSocket::bind("127.0.0.1:0").map_err(|e| format!("could not open the relay's own port: {e}"))?;
        let nexus_addr = from_nexus.local_addr().map_err(|e| e.to_string())?;
        // To the destinations: loopback when they all are, so nothing off this computer can
        // reach it; all interfaces only when the operator chose a destination elsewhere.
        let bind = if targets.iter().all(|t| t.ip().is_loopback()) { "127.0.0.1:0" } else { "0.0.0.0:0" };
        let out = UdpSocket::bind(bind).map_err(|e| format!("could not open the sending port: {e}"))?;
        from_nexus.set_read_timeout(Some(Duration::from_millis(250))).map_err(|e| e.to_string())?;
        out.set_read_timeout(Some(Duration::from_millis(250))).map_err(|e| e.to_string())?;

        let heard: Vec<Arc<Heard>> = targets.iter().map(|a| Arc::new(Heard { addr: *a, last_unix: AtomicU64::new(0) })).collect();
        let counters = Arc::new(Counters::default());
        let stop = Arc::new(AtomicBool::new(false));
        let last_error = Arc::new(Mutex::new(None));

        // Outbound: every datagram from Nexus goes to every destination, unchanged.
        {
            let (out, heard, counters, stop, last_error) = (out.try_clone().map_err(|e| e.to_string())?, heard.clone(), counters.clone(), stop.clone(), last_error.clone());
            std::thread::Builder::new().name("wsjtx-out".into()).spawn(move || {
                let mut buf = vec![0u8; 65_536];
                while !stop.load(Ordering::Relaxed) {
                    let n = match from_nexus.recv_from(&mut buf) {
                        // Only Nexus, on this computer, is listened to.
                        Ok((n, from)) if from.ip().is_loopback() => n,
                        Ok(_) => continue,
                        Err(_) => continue, // timeout, or a transient error: look at `stop` and go on
                    };
                    for t in &heard {
                        match out.send_to(&buf[..n], t.addr) {
                            Ok(_) => { counters.forwarded.fetch_add(1, Ordering::Relaxed); }
                            Err(e) => {
                                counters.send_errors.fetch_add(1, Ordering::Relaxed);
                                *last_error.lock().unwrap_or_else(|p| p.into_inner()) = Some(format!("{}: {e}", t.addr));
                            }
                        }
                    }
                }
            }).map_err(|e| e.to_string())?;
        }
        // Inbound: read so the port never fills, note who spoke, obey nothing.
        {
            let (heard, counters, stop) = (heard.clone(), counters.clone(), stop.clone());
            std::thread::Builder::new().name("wsjtx-in-ignored".into()).spawn(move || {
                let mut buf = vec![0u8; 65_536];
                while !stop.load(Ordering::Relaxed) {
                    if let Ok((_, from)) = out.recv_from(&mut buf) {
                        counters.ignored_inbound.fetch_add(1, Ordering::Relaxed);
                        // A program may answer from another port of the same address.
                        for t in heard.iter().filter(|t| t.addr.ip() == from.ip()) {
                            t.last_unix.store(now_unix(), Ordering::Relaxed);
                        }
                    }
                }
            }).map_err(|e| e.to_string())?;
        }
        Ok(Relay { nexus_addr, targets: heard, counters, stop, last_error })
    }

    /// For Jimmy's Options page: what was sent where, and whether anything came back.
    pub fn status(&self) -> serde_json::Value {
        let now = now_unix();
        let targets: Vec<serde_json::Value> = self.targets.iter().map(|t| {
            let last = t.last_unix.load(Ordering::Relaxed);
            serde_json::json!({
                "addr": t.addr.to_string(),
                "heardSecsAgo": if last == 0 { serde_json::Value::Null } else { serde_json::json!(now.saturating_sub(last)) },
            })
        }).collect();
        serde_json::json!({
            "state": "on",
            "targets": targets,
            "sent": self.counters.forwarded.load(Ordering::Relaxed),
            "sendErrors": self.counters.send_errors.load(Ordering::Relaxed),
            "lastSendError": *self.last_error.lock().unwrap_or_else(|p| p.into_inner()),
            "ignoredInbound": self.counters.ignored_inbound.load(Ordering::Relaxed),
        })
    }
}

impl Drop for Relay {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn listener() -> (UdpSocket, SocketAddr) {
        let s = UdpSocket::bind("127.0.0.1:0").unwrap();
        s.set_read_timeout(Some(Duration::from_millis(1500))).unwrap();
        let a = s.local_addr().unwrap();
        (s, a)
    }

    #[test]
    fn destinations_are_parsed_one_or_many_and_each_bad_entry_is_named() {
        let (ok, bad) = parse_targets("127.0.0.1:2237, 127.0.0.1:2333;192.168.1.20:2237  127.0.0.1:2237");
        assert_eq!(ok.len(), 3, "three different destinations; the repeat is one");
        assert!(bad.is_empty());
        let (ok, bad) = parse_targets("localhost:2237, 127.0.0.1, 127.0.0.1:0, 0.0.0.0:2237, 127.0.0.1:99999");
        assert!(ok.is_empty(), "{ok:?}");
        assert_eq!(bad.len(), 5, "{bad:?}");
        assert_eq!(parse_targets("").0.len(), 0);
    }

    // A fake Nexus sends; two fake programs both get every datagram, byte for byte, in order.
    #[test]
    fn every_datagram_reaches_every_destination_unchanged() {
        let (a, a_addr) = listener();
        let (b, b_addr) = listener();
        let relay = Relay::start(vec![a_addr, b_addr]).unwrap();
        let nexus = UdpSocket::bind("127.0.0.1:0").unwrap();
        let sent: Vec<Vec<u8>> = vec![b"heartbeat".to_vec(), b"status".to_vec(), vec![0xAD, 0xBC, 0xCB, 0xDA, 0, 0, 0, 2, 5], b"logged adif".to_vec()];
        for d in &sent {
            nexus.send_to(d, relay.nexus_addr).unwrap();
        }
        let mut buf = [0u8; 2048];
        for s in [&a, &b] {
            for d in &sent {
                let (n, _) = s.recv_from(&mut buf).expect("a datagram for each one sent");
                assert_eq!(&buf[..n], &d[..]);
            }
        }
        let st = relay.status();
        assert_eq!(st["sent"], 8, "four datagrams to two destinations");
        assert_eq!(st["ignoredInbound"], 0);
        assert!(st["targets"][0]["heardSecsAgo"].is_null(), "sent is not received: nothing claims the program got it");
    }

    // The safety property: what another program sends back never reaches Nexus's socket.
    #[test]
    fn what_another_program_sends_back_never_reaches_the_engine() {
        let (program, program_addr) = listener();
        let relay = Relay::start(vec![program_addr]).unwrap();
        // The fake Nexus: the only socket whose inbound datagrams would be obeyed.
        let nexus = UdpSocket::bind("127.0.0.1:0").unwrap();
        nexus.set_read_timeout(Some(Duration::from_millis(700))).unwrap();
        nexus.send_to(b"status", relay.nexus_addr).unwrap();
        let mut buf = [0u8; 2048];
        let (_, relay_out) = program.recv_from(&mut buf).expect("the program sees the datagram");
        assert_ne!(relay_out, nexus.local_addr().unwrap(), "the program never learns the engine's own port");
        // The program answers the only address it knows, as JTAlert or GridTracker would
        // (a Reply, a HaltTx, free text: the relay does not care which).
        for cmd in [&b"REPLY K1ABC"[..], &b"HALT"[..], &b"FREETEXT CQ"[..]] {
            program.send_to(cmd, relay_out).unwrap();
        }
        assert!(nexus.recv_from(&mut buf).is_err(), "nothing a program sends is passed to the engine");
        std::thread::sleep(Duration::from_millis(400));
        let st = relay.status();
        assert_eq!(st["ignoredInbound"], 3, "they are counted, and dropped");
        assert!(!st["targets"][0]["heardSecsAgo"].is_null(), "and the status can say the program is there");
    }

    #[test]
    fn a_destination_nobody_listens_on_is_not_reported_as_connected() {
        let gone = { let (_s, a) = listener(); a }; // closed again at once
        let relay = Relay::start(vec![gone]).unwrap();
        let nexus = UdpSocket::bind("127.0.0.1:0").unwrap();
        nexus.send_to(b"status", relay.nexus_addr).unwrap();
        std::thread::sleep(Duration::from_millis(500));
        let st = relay.status();
        assert!(st["targets"][0]["heardSecsAgo"].is_null(), "nothing was heard from it");
        assert!(Relay::start(vec![]).is_err(), "no destination: no relay");
    }
}
