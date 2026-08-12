# nethermind-lean — EIP-8288 Lean verifier FFI shim (placeholder)

A C-ABI native library standing in for the Lean Ethereum verifiers behind
[EIP-8288](https://eips.ethereum.org/EIPS/eip-8288), so the entire native-binding path can be
exercised on a live Nethermind node **before** the real Lean tooling stabilizes.

> ⚠️ **Not cryptography.** A "proof" here is `keccak256` of its public inputs — identical to the C#
> `PlaceholderLeanProofVerifier`, so proofs produced by either side verify on the other. It proves the
> *plumbing*, not soundness.

## Layout / swap-out point
- `src/lib.rs` — `extern "C"` surface (`nlean_verify_leansphincs` / `_leanstark` / `_recursive`,
  `nlean_prove_recursive`, `nlean_abi_version`). **This signature is what stays put.**
- `src/lib.rs::verify` — the placeholder relation. **This is the only thing that changes** when the
  real verifier lands: add `leanSig` / `leanVM` as crate deps and replace the three `*_tag` bodies
  with real verification, deserializing the wire bytes inside Rust. The C ABI and the C# consumer
  (`NativeLeanProofVerifier`) are unaffected.

## Build
```bash
cargo build --release --manifest-path tools/lean-ffi/Cargo.toml
# -> tools/lean-ffi/target/release/libnethermind_lean.{dylib,so,dll}
```

## Live FFI test (runs the real P/Invoke)
```bash
dotnet test src/Nethermind/Nethermind.Crypto.LeanFfi.Test/Nethermind.Crypto.LeanFfi.Test.csproj -c release
```
The test project builds the crate, copies the native lib next to the test assembly, and asserts that
managed-produced proofs verify natively and tampered ones are rejected. (Standalone — not part of
`Nethermind.slnx`, so the main build/CI is unaffected.)

## Enable in a running node (devnet)
1. Build the lib and place it where the runtime resolves natives, e.g.
   `bin/runtimes/<rid>/native/libnethermind_lean.<ext>` next to the Nethermind binary.
2. Run with `NETHERMIND_EIP8288_NATIVE_LEAN=1`. `BlockProcessingModule` then registers
   `NativeLeanProofVerifier` and `BlockValidator` uses it instead of the in-process placeholder.
   (Unset ⇒ placeholder; the node runs without the native lib present.)

## Productionization (when real bindings exist)
- **Cross-RID build**: build the cdylib for `linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64`
  (cargo + `cross`/CI), exactly as `blst`/`c-kzg` native libs are shipped today.
- **NuGet**: package as `Nethermind.Crypto.Lean.Bindings` with per-RID `runtimes/<rid>/native/`
  assets, mirroring `Nethermind.Crypto.Bls` / `Ckzg.Bindings`; reference it from `Nethermind.Crypto`
  and drop the in-repo dylib copy.
- The real verifier belongs upstream in `leanEthereum` as a shared `extern "C"` surface (used by all
  non-Rust clients); this crate is the stand-in until then.
