//! FT8-period-aligned audio capture, built on tempo_audio::device::CpalBackend -- real
//! sound-card I/O (device enumeration, mono downmix, anti-aliased resample to 12 kHz), reused
//! directly rather than reimplemented (Nexus's own production capture path).
//!
//! This module owns only the FT8-specific windowing on top: accumulating CpalBackend's captured
//! samples and, for a given UTC period boundary, handing back exactly the 15-second frame
//! decode_frame expects -- anchored to wall-clock time (not "whenever we happened to poll"), so
//! a signal starting exactly on the period boundary lands PREROLL_SECS into the returned buffer,
//! matching decode_frame's own dt=0 convention. Verified live 2026-08-05 against a real radio on
//! 14.074 MHz (20m FT8): pre-fix, real off-air decodes came back with dt systematically around
//! -0.9 to -1.0s (this module was slicing "the last 15s ending now", not anchoring to the actual
//! period start); this rewrite fixes that by tracking a (wall-clock time, total samples pushed)
//! anchor on every poll and computing each period's window from it.

use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tempo_audio::backend::AudioBackend;
use tempo_audio::device::CpalBackend;

use crate::ft8_ffi::FT8_NMAX;

pub const FT8_PERIOD_SECS: u64 = 15;
const SAMPLE_RATE: f64 = 12_000.0;
/// How long before the period boundary the decode window starts, matching the synthesized
/// proof-of-concept's own convention (a signal placed 0.5s into an FT8_NMAX buffer decoded at
/// dt≈0 -- see Phase 4c's main.rs) and WSJT-X's own pre-roll margin.
const PREROLL_SECS: f64 = 0.5;
/// Samples retained behind the write head: must cover at least one full frame's worth (so a
/// just-elapsed period can still be extracted) plus slack for poll-loop jitter.
const RETAIN_SAMPLES: u64 = (FT8_NMAX as u64) * 3;

/// Accumulates 12 kHz mono samples from a real capture device and serves up
/// period-boundary-aligned FT8 decode frames.
pub struct FrameCapture {
    backend: CpalBackend,
    buf: Vec<f32>,
    /// Absolute sample index of `buf[0]` (samples before this have been dropped).
    dropped: u64,
    /// Total samples ever appended (== dropped + buf.len()).
    total_pushed: u64,
    /// (wall-clock time, total_pushed) as of the most recent `poll()` that actually received
    /// samples -- the anchor `frame_for_period` interpolates from.
    anchor: Option<(SystemTime, u64)>,
    /// Peak |sample| seen in the most recent `poll()` call's newly-arrived chunk, independent
    /// of CpalBackend's own decaying `rx_level` meter -- lets a caller tell "genuinely silent
    /// input" apart from "the meter itself isn't updating" during a live-hardware smoke test.
    last_poll_peak: f32,
}

impl FrameCapture {
    pub fn open_default() -> Result<Self, String> {
        Ok(Self::wrap(CpalBackend::open_default()?))
    }

    /// Open a specific named input device (output device left at system default -- this
    /// engine host is RX-only so far, Phase 4d has no TX audio path yet).
    pub fn open_named(in_name: &str) -> Result<Self, String> {
        Ok(Self::wrap(CpalBackend::open(Some(in_name), None)?))
    }

    fn wrap(backend: CpalBackend) -> Self {
        Self { backend, buf: Vec::new(), dropped: 0, total_pushed: 0, anchor: None, last_poll_peak: 0.0 }
    }

    /// Pull whatever's arrived since the last call. Call this frequently (e.g. every
    /// 100-200ms) -- CpalBackend's own ring is unbounded, so an idle caller would otherwise
    /// let it grow without limit, and window alignment accuracy depends on polling often
    /// enough that the anchor stays current.
    pub fn poll(&mut self) {
        let mut chunk = self.backend.capture();
        if chunk.is_empty() {
            return;
        }
        self.last_poll_peak = chunk.iter().fold(0.0f32, |m, &s| m.max(s.abs()));
        self.total_pushed += chunk.len() as u64;
        self.buf.append(&mut chunk);
        self.anchor = Some((SystemTime::now(), self.total_pushed));

        if self.buf.len() as u64 > RETAIN_SAMPLES {
            let excess = (self.buf.len() as u64 - RETAIN_SAMPLES) as usize;
            self.buf.drain(0..excess);
            self.dropped += excess as u64;
        }
    }

    /// Smoothed RX input RMS (0.0-1.0) -- proves real signal is flowing (vs. a dead/silent
    /// stream) without needing a decode to confirm it.
    pub fn rx_level(&self) -> f32 {
        self.backend.rx_level()
    }

    /// See `last_poll_peak`'s doc comment.
    pub fn raw_peak_abs(&self) -> f32 {
        self.last_poll_peak
    }

    /// The decode window for the FT8 period starting at `period_boundary` (a UTC instant at an
    /// exact multiple of FT8_PERIOD_SECS since the epoch): FT8_NMAX samples starting
    /// PREROLL_SECS before the boundary, scaled to i16 the way decode_frame expects. `None` if
    /// too early (not enough samples captured yet for this period) or too late (the needed
    /// samples have already been evicted -- call more often).
    pub fn frame_for_period(&self, period_boundary: SystemTime) -> Option<Vec<i16>> {
        let (anchor_time, anchor_total) = self.anchor?;
        let window_start_time = period_boundary.checked_sub(Duration::from_secs_f64(PREROLL_SECS))?;
        // anchor_time is "now" (or the last poll); window_start_time is in the past relative to it
        // for any period we'd realistically be asked about, so this duration is always forward-going.
        let delta_secs = anchor_time.duration_since(window_start_time).ok()?.as_secs_f64();
        let window_start_abs = anchor_total as i64 - (delta_secs * SAMPLE_RATE).round() as i64;
        if window_start_abs < 0 {
            return None;
        }
        let window_start_abs = window_start_abs as u64;
        if window_start_abs < self.dropped {
            return None; // needed samples already evicted -- poll() more often
        }
        let start_in_buf = (window_start_abs - self.dropped) as usize;
        let end_in_buf = start_in_buf + FT8_NMAX;
        if end_in_buf > self.buf.len() {
            return None; // this period isn't fully captured yet
        }
        Some(
            self.buf[start_in_buf..end_in_buf]
                .iter()
                .map(|&s| (s * 32767.0).clamp(-32768.0, 32767.0) as i16)
                .collect(),
        )
    }
}

/// The current FT8 period index (whole periods since the Unix epoch) and how far into it we
/// are, in seconds. Matches WSJT-X's UTC-aligned :00/:15/:30/:45 convention.
pub fn period_position() -> (u64, f64) {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs_f64();
    let period = FT8_PERIOD_SECS as f64;
    ((secs / period) as u64, secs % period)
}

/// The wall-clock instant a given period index begins.
pub fn period_boundary_time(period_index: u64) -> SystemTime {
    UNIX_EPOCH + Duration::from_secs(period_index * FT8_PERIOD_SECS)
}
