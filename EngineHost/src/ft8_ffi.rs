//! Raw FFI bindings to libtempo's FT8 entry points, replicated directly from the open-source
//! Nexus project's crates/tempo-fast-sys/src/lib.rs (function signatures and the Ft8DecodeT
//! struct layout) rather than depending on that crate -- see build.rs for why (a \\?\-long-path
//! interop bug in that crate's own build.rs in this environment, not a code issue). The struct
//! layout and function signatures are copied verbatim/documented as matching libtempo.h; only
//! the linking mechanism differs from Nexus's own crate.
#![allow(non_camel_case_types)]

use std::ffi::c_char;
use std::os::raw::{c_float, c_int};
use std::sync::{Mutex, MutexGuard};

/// Serializes ALL access to the non-thread-safe libtempo modem (process-global Fortran SAVE
/// state + cached FFTW plans). Every call into libtempo must hold this for the FFI call's
/// duration. Mirrors tempo-fast-sys::MODEM_LOCK / modem_lock().
pub static MODEM_LOCK: Mutex<()> = Mutex::new(());

pub fn modem_lock() -> MutexGuard<'static, ()> {
    MODEM_LOCK.lock().unwrap_or_else(|e| e.into_inner())
}

pub const FT8_NN: usize = 79;
pub const FT8_NMAX: usize = 180_000;
pub const FT8_NZ: usize = 151_680;
pub const SAMPLE_RATE: f32 = 12_000.0;

/// One decode from ft8_decode_frame. Layout matches ft8_decode_t in libtempo.h (64 bytes,
/// 4-byte aligned).
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct Ft8DecodeT {
    pub sync: c_float,
    pub snr: c_int,
    pub dt: c_float,
    pub freq: c_float,
    pub message: [u8; 38],
    pub nap: c_int,
    pub qual: c_float,
}

impl Default for Ft8DecodeT {
    fn default() -> Self {
        Self { sync: 0.0, snr: 0, dt: 0.0, freq: 0.0, message: [0; 38], nap: 0, qual: 0.0 }
    }
}

extern "C" {
    fn ft8_encode(msg: *const c_char, msg_len: c_int, itone_out: *mut c_int, nsym_out: *mut c_int);
    fn ft8_gen_wave(itone: *const c_int, nsym: c_int, fsample: c_float, f0: c_float, wave_out: *mut c_float, nwave_out: *mut c_int);
    #[allow(clippy::too_many_arguments)]
    fn ft8_decode_frame(
        iwave: *const i16, nfa: c_int, nfb: c_int, ndepth: c_int,
        mycall: *const c_char, hiscall: *const c_char,
        nqso_progress: c_int, nfqso: c_int, nutc: c_int,
        la7final: c_int, lft8apon: c_int, lapcqonly: c_int,
        out: *mut Ft8DecodeT, max_out: c_int,
    ) -> c_int;
}

/// Safe wrapper, same shape as the ft8 crate's own `encode`.
pub fn encode(msg: &str) -> Vec<i32> {
    let bytes = msg.as_bytes();
    let mut itone = vec![0i32; FT8_NN];
    let mut nsym: i32 = 0;
    let _guard = modem_lock();
    unsafe {
        ft8_encode(bytes.as_ptr() as *const c_char, bytes.len() as i32, itone.as_mut_ptr(), &mut nsym);
    }
    if nsym < 0 { return Vec::new(); }
    if (nsym as usize) <= itone.len() { itone.truncate(nsym as usize); }
    itone
}

/// Safe wrapper, same shape as the ft8 crate's own `gen_wave`.
pub fn gen_wave(itone: &[i32], fsample: f32, f0: f32) -> Vec<f32> {
    let cap = itone.len() * (FT8_NZ / FT8_NN);
    let mut wave = vec![0f32; cap];
    let mut nwave: i32 = cap as i32;
    let _guard = modem_lock();
    unsafe {
        ft8_gen_wave(itone.as_ptr(), itone.len() as i32, fsample, f0, wave.as_mut_ptr(), &mut nwave);
    }
    if nwave >= 0 && (nwave as usize) <= wave.len() { wave.truncate(nwave as usize); }
    wave
}

#[derive(Debug, Clone)]
pub struct Decode {
    pub message: String,
    pub sync: f32,
    pub snr: i32,
    pub dt: f32,
    pub freq: f32,
    pub nap: i32,
    pub qual: f32,
}

fn cstr_field(buf: &[u8]) -> String {
    let bytes: Vec<u8> = buf.iter().take_while(|&&b| b != 0).copied().collect();
    String::from_utf8_lossy(&bytes).trim_end().to_string()
}

/// Safe wrapper, same shape/argument order as the ft8 crate's own `decode_frame`.
#[allow(clippy::too_many_arguments)]
pub fn decode_frame(
    iwave: &[i16], nfa: i32, nfb: i32, ndepth: i32,
    mycall: &str, hiscall: &str, nqso_progress: i32, nfqso: i32,
    ap: bool, ap_cq_only: bool,
) -> Vec<Decode> {
    assert!(iwave.len() >= FT8_NMAX, "decode_frame needs at least {FT8_NMAX} samples, got {}", iwave.len());
    let myc = std::ffi::CString::new(mycall).unwrap_or_default();
    let hisc = std::ffi::CString::new(hiscall).unwrap_or_default();
    const MAX_DECODES: usize = 200;
    let mut out = vec![Ft8DecodeT::default(); MAX_DECODES];

    let n = {
        let _guard = modem_lock();
        unsafe {
            ft8_decode_frame(
                iwave.as_ptr(), nfa, nfb, ndepth,
                myc.as_ptr(), hisc.as_ptr(),
                nqso_progress, nfqso, 0, true as c_int,
                ap as c_int, ap_cq_only as c_int,
                out.as_mut_ptr(), out.len() as i32,
            )
        }
    };
    if n <= 0 { return Vec::new(); }
    out.into_iter().take(n as usize).map(|r| Decode {
        message: cstr_field(&r.message), sync: r.sync, snr: r.snr, dt: r.dt,
        freq: r.freq, nap: r.nap, qual: r.qual,
    }).collect()
}
