//! Bench test for Stage 1 of transmit-control wiring (tx_control.rs): sends a synthetic Reply
//! datagram, then a HaltTx, to a running jimmy-engine-host instance and lets the operator watch
//! its console output confirm it understood both correctly. No audio, no PTT -- this only
//! exercises the receive/parse/sequence path.
//!
//! Wire format hand-built here (not reusing tempo_net's own private QdsWriter test helper) --
//! verified byte-for-byte against tempo-net/src/wsjtx.rs's own `parse_reply_roundtrip` and
//! `parse_halt_tx` unit tests, which is the authoritative spec for this format.
//!
//! `cargo run --release --example send_reply <engine_addr>` -- engine_addr is the address
//! jimmy-engine-host prints itself at startup ("listening on 127.0.0.1:PORT"), NOT Jimmy's own
//! port: Jimmy replies to whatever address a Heartbeat arrived from, so the engine's own
//! (ephemeral) bound port IS its control address.

use std::net::UdpSocket;
use std::time::Duration;

fn put_u32(buf: &mut Vec<u8>, v: u32) {
    buf.extend_from_slice(&v.to_be_bytes());
}
fn put_i32(buf: &mut Vec<u8>, v: i32) {
    buf.extend_from_slice(&v.to_be_bytes());
}
fn put_f64(buf: &mut Vec<u8>, v: f64) {
    buf.extend_from_slice(&v.to_be_bytes());
}
fn put_bool(buf: &mut Vec<u8>, v: bool) {
    buf.push(if v { 1 } else { 0 });
}
fn put_u8(buf: &mut Vec<u8>, v: u8) {
    buf.push(v);
}
fn put_utf8(buf: &mut Vec<u8>, s: &str) {
    put_u32(buf, s.len() as u32);
    buf.extend_from_slice(s.as_bytes());
}

const MAGIC: u32 = 0xADBCCBDA;
const SCHEMA: u32 = 3;
const MSG_TYPE_REPLY: u32 = 4;
const MSG_TYPE_HALT_TX: u32 = 8;

fn build_reply(id: &str, snr: i32, delta_time: f64, delta_freq: u32, mode: &str, message: &str) -> Vec<u8> {
    let mut b = Vec::new();
    put_u32(&mut b, MAGIC);
    put_u32(&mut b, SCHEMA);
    put_u32(&mut b, MSG_TYPE_REPLY);
    put_utf8(&mut b, id);
    put_u32(&mut b, 0); // time_ms -- unused by tx_control.rs
    put_i32(&mut b, snr);
    put_f64(&mut b, delta_time);
    put_u32(&mut b, delta_freq);
    put_utf8(&mut b, mode);
    put_utf8(&mut b, message);
    put_bool(&mut b, false); // low_confidence
    put_u8(&mut b, 0); // modifiers
    b
}

fn build_halt_tx(id: &str, auto_only: bool) -> Vec<u8> {
    let mut b = Vec::new();
    put_u32(&mut b, MAGIC);
    put_u32(&mut b, SCHEMA);
    put_u32(&mut b, MSG_TYPE_HALT_TX);
    put_utf8(&mut b, id);
    put_bool(&mut b, auto_only);
    b
}

fn main() {
    let engine_addr = match std::env::args().nth(1) {
        Some(a) => a,
        None => {
            eprintln!("usage: send_reply <engine_addr>  (from jimmy-engine-host's own \"listening on\" line)");
            std::process::exit(1);
        }
    };

    let no_halt = std::env::args().any(|a| a == "--no-halt");

    let sock = UdpSocket::bind("127.0.0.1:0").expect("bind ephemeral UDP socket");

    let reply = build_reply("send_reply_bench", -12, 0.2, 1500, "FT8", "CQ K1ABC FN20");
    sock.send_to(&reply, &engine_addr).expect("send Reply");
    println!("Sent synthetic Reply (CQ K1ABC FN20) to {engine_addr} -- watch the engine's own console.");

    if no_halt {
        println!("--no-halt set: skipping HaltTx (for tests that need to watch a TX_SCHEDULE tick).");
        return;
    }

    std::thread::sleep(Duration::from_secs(3));

    let halt = build_halt_tx("send_reply_bench", false);
    sock.send_to(&halt, &engine_addr).expect("send HaltTx");
    println!("Sent HaltTx to {engine_addr} -- watch the engine's own console.");
}
