//! DXCC shadow-comparison tooling (not a pass/fail test): dumps Nexus's
//! `propagation::dxcc::resolve()` output for a fixed, edge-case-heavy callsign list, in a
//! format a companion C# tool (JimmyTests/DxccShadowDump.cs) can diff against Jimmy's own
//! `ClubLogProvider`. See EngineHost/nexus-compat/ or ARCHITECTURE.md for why this exists --
//! deciding whether Jimmy's current DXCC/country/continent resolution should adopt Nexus's
//! local resolver, without regressing award-triggering accuracy.
//!
//! Run with: cargo test --test dxcc_shadow_dump -- --nocapture

const CALLS: &[&str] = &[
    // Common US call areas
    "W1AW", "K9ABC", "N4XYZ", "AA1AA",
    // KG4 format rule: two-letter suffix = Guantanamo Bay, anything else = ordinary USA.
    // This is a hand-coded exception in Jimmy's ClubLogProvider (ResolveKg4Prefix) --
    // the sharpest edge case in this whole comparison.
    "KG4AB", "KG4XYZ", "KG4JOK",
    // K4 prefix: must resolve to current USA, not the expired 1946 Puerto Rico assignment
    // Jimmy's own code specifically fixed a live misclassification for.
    "K4YT", "K4ABC",
    // US territories (own DXCC entities, distinct from the mainland USA entity)
    "NP4TX", "KP4AA",
    // Hawaii/Alaska: part of the single USA DXCC entity, not separate entities
    "KH6XX", "KL7AA",
    // International spread across continents
    "G3ABC", "JA1ABC", "VK2ABC", "ZS6ABC", "PY2ABC", "9V1ABC", "4X1ABC",
    "VE3ABC", "VE7ABC",
    // Portable/compound suffixes
    "W1AW/P", "W1AW/MM", "DL/W1AW",
    // cty.dat exact-call override -- Nexus's own dxcc.rs doc comment names this exact
    // example (Bouvet, which has no plain "3Y" prefix of its own).
    "3Y0J",
    // Jimmy's own existing test-fixture calls (TestFixtureLookupProvider.cs) -- reusing
    // them here checks Nexus's resolver against Jimmy's own established "ground truth".
    "K1ABC/H", "W5HRC", "G3HRC", "K5SNL", "PY5SNL", "K3ZK",
    // Deliberately unresolvable
    "ZZZZZ99",
];

#[test]
fn dump_dxcc_shadow_comparison() {
    println!("CALL|ENTITY|CONT|CQ_ZONE|IS_DXCC");
    for &call in CALLS {
        match propagation::dxcc::resolve(call) {
            Some(info) => println!(
                "{call}|{}|{}|{}|{}",
                info.entity, info.cont, info.cq_zone, info.is_dxcc
            ),
            None => println!("{call}|<NONE>|||"),
        }
    }
}
