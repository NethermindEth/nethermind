//! Locates a prebuilt Nethermind EVM bundle and links against it.
//!
//! Resolution order:
//!   1. `NETHERMIND_EVM_DIR`  — a bundle directory produced by tools/nethermind-evm-ffi/package.sh
//!   2. `vendor/` beside this crate — for a bundle checked in or unpacked next to the source
//!
//! An rpath is baked in so the consuming binary finds both the library and the native libraries
//! its precompiles dlopen at run time, without LD_LIBRARY_PATH.

use std::path::{Path, PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=NETHERMIND_EVM_DIR");

    let bundle = locate().unwrap_or_else(|| {
        panic!(
            "no Nethermind EVM bundle found.\n\
             Set NETHERMIND_EVM_DIR to a bundle directory, or place one in vendor/ beside this crate.\n\
             Build one with: tools/nethermind-evm-ffi/package.sh"
        )
    });

    let lib = bundle.join("lib");
    assert!(
        lib.join("libnethermind_evm.so").exists(),
        "{} does not contain lib/libnethermind_evm.so",
        bundle.display()
    );

    println!("cargo:rustc-link-search=native={}", lib.display());
    println!("cargo:rustc-link-lib=dylib=nethermind_evm");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib.display());
    // Consumers of this -sys crate get the directory, so a safe wrapper can find the natives too.
    println!("cargo:bundle={}", bundle.display());
    println!("cargo:lib={}", lib.display());
    println!("cargo:rerun-if-changed={}", lib.join("libnethermind_evm.so").display());
}

fn locate() -> Option<PathBuf> {
    if let Ok(dir) = std::env::var("NETHERMIND_EVM_DIR") {
        let path = PathBuf::from(dir);
        if valid(&path) {
            return Some(path);
        }
        panic!(
            "NETHERMIND_EVM_DIR points at {}, which has no lib/libnethermind_evm.so",
            path.display()
        );
    }
    let vendored = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("vendor");
    valid(&vendored).then_some(vendored)
}

fn valid(dir: &Path) -> bool {
    dir.join("lib").join("libnethermind_evm.so").is_file()
}
