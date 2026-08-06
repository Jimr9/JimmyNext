// Links directly against the libtempo static archive already built manually at
// C:\claude\nexus\libtempo\build (via `cmake -B build -G Ninja` from within libtempo/, native
// UCRT64 build, no cross-compile toolchain file) -- sidesteps tempo-fast-sys's own build.rs,
// which hits a \\?\-long-path interop bug between cmake/ninja/gfortran in this environment when
// it calls .canonicalize() on the source path. Same link directives tempo-fast-sys's own
// build.rs uses for its native (non-cross) path: static libtempo first, then its dynamic
// runtime dependencies (gfortran/fftw3f/stdc++/m/quadmath) -- meaning this binary needs the
// UCRT64 runtime DLLs on PATH at runtime, same as libtempo's own roundtrip.exe etc.
fn main() {
    println!("cargo:rustc-link-search=native=C:/claude/nexus/libtempo/build");
    println!("cargo:rustc-link-lib=static=tempo");
    println!("cargo:rustc-link-lib=dylib=gfortran");
    println!("cargo:rustc-link-lib=dylib=fftw3f");
    println!("cargo:rustc-link-lib=dylib=stdc++");
    println!("cargo:rustc-link-lib=dylib=m");
    println!("cargo:rustc-link-lib=dylib=quadmath");
}
