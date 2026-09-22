//! Because nethermind-evm-sys declares `links = "nethermind_evm"`, Cargo hands its build-script
//! metadata to direct dependents as DEP_NETHERMIND_EVM_*. Link args do not propagate across
//! crates, so the binary has to bake in the rpath itself. Any consumer — rbuilder included —
//! needs exactly this.
fn main() {
    let lib = std::env::var("DEP_NETHERMIND_EVM_LIB")
        .expect("nethermind-evm-sys did not publish its library path");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{lib}");
}
