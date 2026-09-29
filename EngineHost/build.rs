// 2026-09-28: embed the Jimmy antenna icon (repo root jimmy.ico) in jimmy-engine-host.exe.
// Windows shows this process -- the one using the sound card -- in the tray's microphone
// indicator and the Volume Mixer, where it had a blank icon. Uses windres (MSYS2 ucrt64, already
// on PATH for this GNU-target build); if it is missing the exe still builds, just without an icon.
use std::{env, path::PathBuf, process::Command};

fn main() {
    let manifest = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let ico = manifest.join("..").join("jimmy.ico");
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed={}", ico.display());
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") || !ico.exists() {
        return;
    }
    let out = PathBuf::from(env::var("OUT_DIR").unwrap());
    let rc = out.join("icon.rc");
    let obj = out.join("icon.o");
    std::fs::write(&rc, format!("1 ICON \"{}\"\n", ico.display().to_string().replace('\\', "/"))).unwrap();
    match Command::new("windres").arg(&rc).args(["-O", "coff", "-o"]).arg(&obj).status() {
        Ok(s) if s.success() => println!("cargo:rustc-link-arg-bins={}", obj.display()),
        _ => println!("cargo:warning=windres unavailable: jimmy-engine-host.exe built without an icon"),
    }
}
