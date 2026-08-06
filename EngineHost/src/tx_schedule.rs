//! TX slot-parity scheduling: which 15-second FT8 periods are "mine" to transmit on for a given
//! QSO. Stage 2 of transmit-control wiring (see main.rs's own header comment for the staged
//! build-out) -- this module only computes and reports the schedule; nothing here plays audio or
//! asserts PTT.
//!
//! The algorithm is Nexus's own documented "smart auto-cycle" (tempo_app::engine::Engine::
//! send_message, `tx_cycle_auto` path): answer a heard station on the OPPOSITE T/R parity to the
//! one it was decoded in, so you key while they listen -- WSJT-X's own standard 50/50-collision
//! avoidance. Reimplemented here as a small, standalone calculation (not a dependency on
//! tempo_app::Engine, a ~20,000-line Nexus-UI-coupled orchestrator covering CW keyers, satellite
//! uplinks, band plans, radio profiles etc. that doesn't fit this engine host at all -- see the
//! Phase 4e commit for the same reasoning applied to tempo_core::qso::Station).

use tempo_core::qso::Station;

pub struct ActiveQso {
    pub station: Station,
    /// 0 = transmit on even-indexed periods, 1 = odd. Fixed for the life of one QSO -- once set
    /// from the period the initiating decode was heard in, both sides naturally keep alternating
    /// (the DX's own replies land in the opposite parity from ours automatically, since they're
    /// replying to messages WE sent in our own parity).
    pub tx_parity: u64,
    /// The period this QSO's audio was last (would have been, Stage 2) transmitted for -- so a
    /// matching period is only acted on once, not every poll tick while it remains current.
    pub last_tx_period: Option<u64>,
}

/// The TX parity for a QSO responding to a signal decoded in `decoded_period`: the opposite of
/// that period's own parity, so the reply keys while the original sender is listening.
pub fn tx_parity_for_decoded_period(decoded_period: u64) -> u64 {
    1 - (decoded_period % 2)
}

/// True when `period` belongs to the given TX parity (0 or 1).
pub fn period_matches_parity(period: u64, tx_parity: u64) -> bool {
    period % 2 == tx_parity
}

impl ActiveQso {
    pub fn new(station: Station, decoded_period: u64) -> Self {
        Self { station, tx_parity: tx_parity_for_decoded_period(decoded_period), last_tx_period: None }
    }

    /// Whether this QSO owes a transmission for `period` that it hasn't already handled.
    pub fn owes_tx_for(&self, period: u64) -> bool {
        self.station.pending_text().is_some()
            && period_matches_parity(period, self.tx_parity)
            && self.last_tx_period != Some(period)
    }
}
