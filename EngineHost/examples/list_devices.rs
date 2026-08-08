//! Quick manual check: what input/output audio devices does cpal see on this machine?
//! Not part of the shipped binary -- `cargo run --example list_devices`.
fn main() {
    let (inputs, outputs) = tempo_audio::device::available_devices();
    println!("Input devices ({}):", inputs.len());
    for d in &inputs {
        println!("  - {} ({})", d.name, d.label);
    }
    println!("Output devices ({}):", outputs.len());
    for d in &outputs {
        println!("  - {} ({})", d.name, d.label);
    }
}
