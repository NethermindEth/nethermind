// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Links the shared libnethermind_ffi built by ../Makefile (or from NETHERMIND_FFI_LIB_DIR).

use std::env;
use std::path::PathBuf;

fn main() {
    let lib_dir = env::var_os("NETHERMIND_FFI_LIB_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("../build"));
    let lib_dir = lib_dir
        .canonicalize()
        .expect("libnethermind_ffi not built; run make in Nethermind.FFI/native");

    println!("cargo:rerun-if-env-changed=NETHERMIND_FFI_LIB_DIR");
    println!("cargo:rustc-link-search=native={}", lib_dir.display());
    println!("cargo:rustc-link-lib=dylib=nethermind_ffi");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib_dir.display());
}
