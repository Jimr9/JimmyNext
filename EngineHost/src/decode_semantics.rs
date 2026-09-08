//! Nexus modernization Stage 4 (2026-09-08): a small, additive, versioned per-decode
//! "semantic envelope" derived IN ENGINEHOST from Nexus's own PUBLIC message parser
//! (`tempo_core::message`), for the FT8/FT4 facts that Nexus v1.10.3's `DecodeRow`
//! does not expose cleanly and that Jimmy currently re-derives with its own
//! `WsjtxMessage` parser:
//!
//!   * the recipient ("to") call
//!   * the directed-CQ target ("DX" / "NA" / "POTA" / "TEST" / "040" ...)
//!   * the numeric signal report on ANY decode (not just the active QSO's rx_report)
//!   * R-report vs plain report
//!   * RRR distinct from RR73 distinct from 73
//!   * call form (standard / compound / nonstandard)
//!   * the decode's relationship to the active QSO
//!
//! This is NOT a Nexus patch and NOT a reparse of raw text: it calls
//! `tempo_core::message::Msg::parse` -- Nexus's own parser, a public API -- and maps
//! its typed result. It is injected onto the `SNAPSHOT` JSON as a top-level
//! `decodeSemantics` array, one entry per `recentDecodes` entry in the SAME order,
//! exactly like the existing `sessionToken` / `pid` injection (the pinned
//! `AppSnapshot` struct is never touched).
//!
//! Jimmy's `DirectDecodeSemantics` (WsjtxClient.Direct.cs) consumes it BESIDE the
//! `WsjtxMessage` parse for shadow comparison (Stage 5). Nothing acts on it until a
//! consumer is proven equal and migrated (Stages 6+).
//!
//! ## Hashed callsigns (Post-Stage-12 cleanup S1, 2026-09-08)
//!
//! The parse is run through Nexus's OWN `Msg::unhashed()` (a public method on `Msg`,
//! `tempo_core::message`) before `from` / `to` / `call_form` are read, so the envelope
//! carries the callsign Nexus itself would recognise and log -- not the raw i3=4
//! bracket form. `Msg::unhashed()` delegates to `message::resolve_hashed`, whose rules
//! are DELIBERATELY fine-grained and are followed here verbatim (Jim's S1 decision:
//! do not force these to imitate Jimmy's old blanket bracket-strip):
//!
//!   * `<W9XYZ>`  -> `W9XYZ`   -- a STANDARD call sent hashed only to save bits (the
//!                               multi-answering DXpedition case, field report
//!                               2026-08-23 `<RI1FJL> KD9TAW EN52`). Resolved.
//!   * `<KH8/W1AW>` stays `<KH8/W1AW>` -- a COMPOUND call is hashed because it does
//!                               NOT fit an ordinary 77-bit frame; unwrapping it would
//!                               name a call the protocol cannot carry (Nexus issue
//!                               #84). Nexus keeps the brackets all the way to the air;
//!                               so does this envelope. A consumer that compares
//!                               `from`/`to` to a bare compound call MUST do it
//!                               bracket-insensitively (Nexus's `base_call` /
//!                               `same_call` already are; so is Jimmy's callsign
//!                               matching). Jimmy's `NormalizeDecodedMessage` still
//!                               strips these brackets for the RAW `enq.Message` text
//!                               the un-migrated `WsjtxMessage` consumers read -- that
//!                               path is unchanged.
//!   * `<...>`    stays `<...>` -- an UNRESOLVED hash is not a callsign; both Nexus and
//!                               Jimmy already reject it downstream.
//!
//! Matching was never affected (Nexus's `same_call` / `base_call` ignore brackets):
//! this changes what the envelope SAYS a call is, aligning it with `DecodeRow.from` and
//! with Nexus's own sequencer, not who is recognised.
//!
//! Obsoleted when: Nexus's own `DecodeRow` carries typed to / report / kind /
//! call-form directly.

use serde::Serialize;
use tempo_core::message::{self, Msg};

/// Schema version of the envelope shape below. Bump on any breaking field change so a
/// mismatched Jimmy can refuse to shadow-compare rather than compare noise.
pub const SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SemKind {
    Cq,
    DirectedCq,
    /// `<to> <de> <grid>` -- a reply to a CQ / call, carrying a grid.
    Reply,
    /// `<to> <de> <snr>`
    Report,
    /// `<to> <de> R<snr>`
    RReport,
    Rrr,
    Rr73,
    SevenThree,
    FieldDay,
    /// Free text or anything not a recognized standard form.
    Other,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SemSignoff {
    Rrr,
    Rr73,
    SevenThree,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SemCallForm {
    /// Fits the 77-bit protocol's standard 28-bit callsign field (incl. `/P` `/R`).
    Standard,
    /// Has a `/` and a part that is a callsign (`PJ4/K1ABC`, `KD9TAW/QRP`).
    Compound,
    /// A callsign shape the 28-bit field cannot hold -- must be hashed on the air.
    Nonstandard,
    /// Sender not identifiable (free text, or a bare hash we can't read).
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SemQsoRelation {
    /// No active QSO, or neither party is our partner and it is not addressed to us.
    None,
    /// From or to our current QSO partner, with US at the other end.
    Partner,
    /// Our current QSO partner is exchanging with someone else (either direction).
    PartnerWorkingOther,
    /// Neither party is our partner, but the decode is addressed to us.
    AddressedToUsBystander,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DecodeSemantics {
    pub schema_version: u32,
    /// The engine text this was parsed from (as `DecodeRow.message` carries it).
    pub raw_message: String,
    pub kind: SemKind,
    pub from: Option<String>,
    pub to: Option<String>,
    /// Directed-CQ token. `Some` only for `DirectedCq`.
    pub cq_direction: Option<String>,
    pub grid: Option<String>,
    /// Numeric report for `Report` / `RReport`; `None` otherwise.
    pub report_db: Option<i32>,
    pub addressed_to_me: bool,
    /// `Some` for a signoff (`Rrr` / `Rr73` / `SevenThree`); mirrors `kind` but is the
    /// single field a signoff-subtype consumer reads.
    pub signoff: Option<SemSignoff>,
    pub call_form: SemCallForm,
    pub qso_relation: SemQsoRelation,
}

impl DecodeSemantics {
    /// Build the envelope for one decode. `my_call` is the operator's callsign;
    /// `partner` is the active QSO partner (`QsoStatus.dxcall`), if any.
    pub fn from_decode(raw_message: &str, my_call: &str, partner: Option<&str>) -> Self {
        // S1 (2026-09-08): resolve i3=4 hashed callsigns with Nexus's OWN `Msg::unhashed()`
        // before reading `from` / `to` / `call_form`, so the envelope names the call Nexus
        // itself recognises (a hashed STANDARD call unwrapped; a COMPOUND or unresolved hash
        // deliberately left bracketed -- see the module header). Nexus applies this same step
        // where IT parses decodes; this brings the envelope into line. No Nexus patch:
        // `Msg::unhashed` is a public method. `Cq` / `Other` pass through untouched.
        let msg = Msg::parse(raw_message).unhashed();
        let from = msg.sender().map(str::to_string);
        let to = msg.addressee().map(str::to_string);

        let (kind, cq_direction, grid, report_db, signoff) = match &msg {
            Msg::Cq { grid, dir, .. } => {
                let k = if dir.is_empty() { SemKind::Cq } else { SemKind::DirectedCq };
                let cd = if dir.is_empty() { None } else { Some(dir.clone()) };
                let g = if grid.is_empty() { None } else { Some(grid.clone()) };
                (k, cd, g, None, None)
            }
            Msg::Grid { grid, .. } => {
                let g = if grid.is_empty() { None } else { Some(grid.clone()) };
                (SemKind::Reply, None, g, None, None)
            }
            Msg::Report { snr, .. } => (SemKind::Report, None, None, Some(*snr), None),
            Msg::RReport { snr, .. } => (SemKind::RReport, None, None, Some(*snr), None),
            Msg::Rrr { .. } => (SemKind::Rrr, None, None, None, Some(SemSignoff::Rrr)),
            Msg::Rr73 { .. } => (SemKind::Rr73, None, None, None, Some(SemSignoff::Rr73)),
            Msg::Bye73 { .. } => {
                (SemKind::SevenThree, None, None, None, Some(SemSignoff::SevenThree))
            }
            Msg::FieldDay { .. } => (SemKind::FieldDay, None, None, None, None),
            Msg::Other(_) => (SemKind::Other, None, None, None, None),
        };

        let addressed_to_me = to
            .as_deref()
            .map_or(false, |t| message::same_call(t, my_call));

        let call_form = match from.as_deref() {
            None => SemCallForm::Unknown,
            Some(c) if message::is_std_call(c) => SemCallForm::Standard,
            Some(c) if message::is_compound(c) => SemCallForm::Compound,
            Some(c) if message::is_nonstandard_call(c) => SemCallForm::Nonstandard,
            Some(_) => SemCallForm::Unknown,
        };

        let qso_relation = match partner {
            None => SemQsoRelation::None,
            Some(p) => {
                let from_is_partner =
                    from.as_deref().map_or(false, |f| message::same_call(f, p));
                let to_is_partner =
                    to.as_deref().map_or(false, |t| message::same_call(t, p));
                if from_is_partner || to_is_partner {
                    let other_is_me = if from_is_partner {
                        to.as_deref().map_or(false, |t| message::same_call(t, my_call))
                    } else {
                        from.as_deref().map_or(false, |f| message::same_call(f, my_call))
                    };
                    if other_is_me {
                        SemQsoRelation::Partner
                    } else {
                        SemQsoRelation::PartnerWorkingOther
                    }
                } else if addressed_to_me {
                    SemQsoRelation::AddressedToUsBystander
                } else {
                    SemQsoRelation::None
                }
            }
        };

        DecodeSemantics {
            schema_version: SCHEMA_VERSION,
            raw_message: raw_message.to_string(),
            kind,
            from,
            to,
            cq_direction,
            grid,
            report_db,
            addressed_to_me,
            signoff,
            call_form,
            qso_relation,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sem(msg: &str) -> DecodeSemantics {
        DecodeSemantics::from_decode(msg, "W9XYZ", None)
    }

    #[test]
    fn plain_cq_has_no_direction_and_carries_the_grid() {
        let s = sem("CQ K1ABC FN42");
        assert_eq!(s.kind, SemKind::Cq);
        assert_eq!(s.from.as_deref(), Some("K1ABC"));
        assert_eq!(s.to, None);
        assert_eq!(s.cq_direction, None);
        assert_eq!(s.grid.as_deref(), Some("FN42"));
        assert_eq!(s.call_form, SemCallForm::Standard);
        assert!(!s.addressed_to_me);
    }

    #[test]
    fn directed_cq_keeps_its_target_token() {
        let s = sem("CQ POTA K1ABC FN42");
        assert_eq!(s.kind, SemKind::DirectedCq);
        assert_eq!(s.cq_direction.as_deref(), Some("POTA"));
        assert_eq!(s.from.as_deref(), Some("K1ABC"));
        let dx = sem("CQ DX K1ABC FN42");
        assert_eq!(dx.cq_direction.as_deref(), Some("DX"));
    }

    #[test]
    fn report_vs_rreport_are_distinct_and_carry_the_number() {
        let r = sem("W9XYZ K1ABC -08");
        assert_eq!(r.kind, SemKind::Report);
        assert_eq!(r.report_db, Some(-8));
        assert_eq!(r.to.as_deref(), Some("W9XYZ"));
        assert!(r.addressed_to_me);
        let rr = sem("W9XYZ K1ABC R-08");
        assert_eq!(rr.kind, SemKind::RReport);
        assert_eq!(rr.report_db, Some(-8));
        assert!(rr.addressed_to_me);
    }

    #[test]
    fn rrr_rr73_and_73_are_three_distinct_signoff_subtypes() {
        assert_eq!(sem("W9XYZ K1ABC RRR").signoff, Some(SemSignoff::Rrr));
        assert_eq!(sem("W9XYZ K1ABC RR73").signoff, Some(SemSignoff::Rr73));
        assert_eq!(sem("W9XYZ K1ABC 73").signoff, Some(SemSignoff::SevenThree));
        assert_eq!(sem("W9XYZ K1ABC RRR").kind, SemKind::Rrr);
        assert_eq!(sem("W9XYZ K1ABC RR73").kind, SemKind::Rr73);
        assert_eq!(sem("W9XYZ K1ABC 73").kind, SemKind::SevenThree);
        // a "DM73" grid reply is NOT a signoff (token-positional, via Nexus's parser)
        let g = sem("W9XYZ K1ABC DM73");
        assert_eq!(g.signoff, None);
        assert_eq!(g.kind, SemKind::Reply);
    }

    #[test]
    fn call_form_classifies_the_sender_standard_vs_compound() {
        // call_form is about the SENDER (`de`). "CQ K1ABC FN42" -> sender K1ABC, standard.
        assert_eq!(sem("CQ K1ABC FN42").call_form, SemCallForm::Standard);
        // "/P" rides its own protocol bit -> still standard.
        assert_eq!(sem("CQ F4CYH/P JN18").call_form, SemCallForm::Standard);
        // "W9XYZ PJ4/K1ABC -08" = PJ4/K1ABC reporting to W9XYZ -> sender is the compound one.
        let s = sem("W9XYZ PJ4/K1ABC -08");
        assert_eq!(s.from.as_deref(), Some("PJ4/K1ABC"));
        assert_eq!(s.to.as_deref(), Some("W9XYZ"));
        assert_eq!(s.call_form, SemCallForm::Compound);
    }

    #[test]
    fn qso_relation_sees_partner_partner_working_other_and_bystander() {
        // our partner is K1ABC
        let p = Some("K1ABC");
        let ours = DecodeSemantics::from_decode("W9XYZ K1ABC -08", "W9XYZ", p);
        assert_eq!(ours.qso_relation, SemQsoRelation::Partner);
        let other = DecodeSemantics::from_decode("K1ABC N0DX RR73", "W9XYZ", p);
        assert_eq!(other.qso_relation, SemQsoRelation::PartnerWorkingOther);
        let toother = DecodeSemantics::from_decode("N0DX K1ABC -08", "W9XYZ", p);
        assert_eq!(toother.qso_relation, SemQsoRelation::PartnerWorkingOther);
        let bystander = DecodeSemantics::from_decode("W9XYZ N0DX RR73", "W9XYZ", p);
        assert_eq!(bystander.qso_relation, SemQsoRelation::AddressedToUsBystander);
        let unrelated = DecodeSemantics::from_decode("CQ N0DX EM10", "W9XYZ", p);
        assert_eq!(unrelated.qso_relation, SemQsoRelation::None);
    }

    #[test]
    fn free_text_is_other_with_unknown_call_form() {
        let s = sem("HELLO WORLD TEST");
        assert_eq!(s.kind, SemKind::Other);
        assert_eq!(s.call_form, SemCallForm::Unknown);
        assert_eq!(s.from, None);
        assert_eq!(s.signoff, None);
    }

    #[test]
    fn the_schema_version_rides_every_entry() {
        assert_eq!(sem("CQ K1ABC FN42").schema_version, SCHEMA_VERSION);
    }

    /// S1 (Post-Stage-12 cleanup, 2026-09-08): the envelope's `from` / `to` / `call_form`
    /// come from Nexus's OWN `Msg::unhashed()` hash resolution. Fine-grained on purpose --
    /// a hashed STANDARD call is unwrapped, a COMPOUND or UNRESOLVED hash keeps its
    /// brackets (Nexus issue #84: a compound call cannot ride an ordinary frame bare).
    #[test]
    fn s1_hash_resolution_follows_nexus_fine_grained_rules() {
        // (a) ordinary call -- untouched, both fields.
        let ord = DecodeSemantics::from_decode("W9XYZ K1ABC -08", "W9XYZ", None);
        assert_eq!(ord.from.as_deref(), Some("K1ABC"));
        assert_eq!(ord.to.as_deref(), Some("W9XYZ"));
        assert_eq!(ord.call_form, SemCallForm::Standard);

        // (b) portable/compound sent IN THE CLEAR (not hashed) -- untouched.
        let port = DecodeSemantics::from_decode("W9XYZ PJ4/K1ABC -08", "W9XYZ", None);
        assert_eq!(port.from.as_deref(), Some("PJ4/K1ABC"));
        assert_eq!(port.call_form, SemCallForm::Compound);
        // "/P" rides its own protocol bit -> standard, and never hashed.
        assert_eq!(
            DecodeSemantics::from_decode("CQ F4CYH/P JN18", "W9XYZ", None).from.as_deref(),
            Some("F4CYH/P")
        );

        // (c) hashed STANDARD call -- Nexus RESOLVES it; the envelope must NOT keep the
        //     brackets (field report 2026-08-23 `<RI1FJL> KD9TAW EN52`). Both roles.
        let hs_from = DecodeSemantics::from_decode("KD9TAW <W9XYZ> EN37", "KD9TAW", None);
        assert_eq!(hs_from.from.as_deref(), Some("W9XYZ"), "hashed standard sender resolved");
        assert_eq!(hs_from.call_form, SemCallForm::Standard);
        let hs_to = DecodeSemantics::from_decode("<W9XYZ> K1ABC -05", "W9XYZ", None);
        assert_eq!(hs_to.to.as_deref(), Some("W9XYZ"), "hashed standard recipient resolved");
        assert!(hs_to.addressed_to_me, "and still reads as addressed to me");
        assert_eq!(hs_to.kind, SemKind::Report);

        // (d) hashed COMPOUND / DXpedition-style call (W1AW/2, KH8/W1AW) -- Nexus
        //     DELIBERATELY keeps the brackets so the compound form never goes on the air
        //     unwrapped. The envelope follows suit. A consumer matching this against a bare
        //     "W1AW/2" must compare bracket-insensitively (Jimmy uses Qso.dxcall for
        //     partner identity in the completion path -- see S3 -- exactly to avoid this).
        let hc = DecodeSemantics::from_decode("<W1AW/2> KB0UZT -07", "KB0UZT", None);
        assert_eq!(hc.to.as_deref(), Some("<W1AW/2>"), "compound hash stays bracketed (Nexus #84)");
        assert_eq!(hc.kind, SemKind::Report, "kind is still positional -> correct despite brackets");
        assert_eq!(hc.report_db, Some(-7));
        assert_eq!(
            DecodeSemantics::from_decode("KB0UZT <KH8/W1AW> R-03", "KB0UZT", None).from.as_deref(),
            Some("<KH8/W1AW>")
        );

        // (e) UNRESOLVED hash sender -- stays `<...>`, never becomes a plausible-looking call.
        let un = DecodeSemantics::from_decode("KD9TAW <...> EN37", "KD9TAW", None);
        assert_eq!(un.to.as_deref(), Some("KD9TAW"));
        assert_eq!(un.from.as_deref(), Some("<...>"));
        assert_eq!(un.call_form, SemCallForm::Unknown);
    }

    /// Stage 5 shadow-comparison corpus, LOCKED to Nexus's actual parse. The Jimmy-side
    /// SemanticShadowCorpusTests (JimmyTests.cs) uses the SAME strings and asserts these
    /// exact values as the "Nexus" side of the diff -- keep the two in sync. `(msg, kind,
    /// from, to, cq_dir, grid, report, addr_to_me, signoff, call_form)` for my_call W9XYZ,
    /// no active QSO.
    #[test]
    fn stage5_corpus_matches_nexus_parse_exactly() {
        let c = |m: &str| DecodeSemantics::from_decode(m, "W9XYZ", None);
        macro_rules! row {
            ($m:expr, $k:expr, $from:expr, $to:expr, $dir:expr, $grid:expr, $rep:expr,
             $atm:expr, $so:expr, $cf:expr) => {{
                let s = c($m);
                assert_eq!(s.kind, $k, "kind for {:?}", $m);
                assert_eq!(s.from.as_deref(), $from, "from for {:?}", $m);
                assert_eq!(s.to.as_deref(), $to, "to for {:?}", $m);
                assert_eq!(s.cq_direction.as_deref(), $dir, "cq_dir for {:?}", $m);
                assert_eq!(s.grid.as_deref(), $grid, "grid for {:?}", $m);
                assert_eq!(s.report_db, $rep, "report for {:?}", $m);
                assert_eq!(s.addressed_to_me, $atm, "addr_to_me for {:?}", $m);
                assert_eq!(s.signoff, $so, "signoff for {:?}", $m);
                assert_eq!(s.call_form, $cf, "call_form for {:?}", $m);
            }};
        }
        use SemCallForm::*;
        use SemKind::*;
        // plain + directed CQ
        row!("CQ K1ABC FN42", Cq, Some("K1ABC"), None, None, Some("FN42"), None, false, None, Standard);
        row!("CQ DX K1ABC FN42", DirectedCq, Some("K1ABC"), None, Some("DX"), Some("FN42"), None, false, None, Standard);
        row!("CQ POTA K1ABC FN42", DirectedCq, Some("K1ABC"), None, Some("POTA"), Some("FN42"), None, false, None, Standard);
        row!("CQ 040 K1ABC FN42", DirectedCq, Some("K1ABC"), None, Some("040"), Some("FN42"), None, false, None, Standard);
        row!("CQ NA K1ABC FN42", DirectedCq, Some("K1ABC"), None, Some("NA"), Some("FN42"), None, false, None, Standard);
        // reply w/ grid, report, R-report
        row!("W9XYZ K1ABC FN31", Reply, Some("K1ABC"), Some("W9XYZ"), None, Some("FN31"), None, true, None, Standard);
        row!("K7QQ K1ABC EM10", Reply, Some("K1ABC"), Some("K7QQ"), None, Some("EM10"), None, false, None, Standard);
        row!("W9XYZ K1ABC -08", Report, Some("K1ABC"), Some("W9XYZ"), None, None, Some(-8), true, None, Standard);
        row!("W9XYZ K1ABC +02", Report, Some("K1ABC"), Some("W9XYZ"), None, None, Some(2), true, None, Standard);
        row!("W9XYZ K1ABC R-08", RReport, Some("K1ABC"), Some("W9XYZ"), None, None, Some(-8), true, None, Standard);
        // signoffs, 3 subtypes, + a DM73 grid that must NOT be a signoff
        row!("W9XYZ K1ABC RRR", Rrr, Some("K1ABC"), Some("W9XYZ"), None, None, None, true, Some(SemSignoff::Rrr), Standard);
        row!("W9XYZ K1ABC RR73", Rr73, Some("K1ABC"), Some("W9XYZ"), None, None, None, true, Some(SemSignoff::Rr73), Standard);
        row!("W9XYZ K1ABC 73", SevenThree, Some("K1ABC"), Some("W9XYZ"), None, None, None, true, Some(SemSignoff::SevenThree), Standard);
        row!("W9XYZ K1ABC DM73", Reply, Some("K1ABC"), Some("W9XYZ"), None, Some("DM73"), None, true, None, Standard);
        // compound / portable senders
        row!("CQ F4CYH/P JN18", Cq, Some("F4CYH/P"), None, None, Some("JN18"), None, false, None, Standard);
        row!("W9XYZ PJ4/K1ABC -08", Report, Some("PJ4/K1ABC"), Some("W9XYZ"), None, None, Some(-8), true, None, Compound);
        // Field Day exchange
        row!("W9XYZ K2DEF 3A WI", FieldDay, Some("K2DEF"), Some("W9XYZ"), None, None, None, true, None, Standard);
        row!("W9XYZ K2DEF R 3A WI", FieldDay, Some("K2DEF"), Some("W9XYZ"), None, None, None, true, None, Standard);
        // free text / not-a-report
        row!("HPE CUAGN OM", Other, None, None, None, None, None, false, None, Unknown);
        row!("W9XYZ K1ABC R73", Other, None, None, None, None, None, false, None, Unknown);
    }
}
