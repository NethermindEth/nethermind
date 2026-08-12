// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! C-ABI shim standing in for the Lean Ethereum verifiers behind EIP-8288.
//!
//! The exported functions mirror `Nethermind.Core.Crypto.ILeanProofVerifier`. The verification is a
//! deterministic PLACEHOLDER — a "proof" is `keccak256` of its public inputs, identical to the C#
//! `PlaceholderLeanProofVerifier`, so proofs produced by either side verify on the other. This is NOT
//! cryptography (a proof is public and trivially forgeable); it exists solely to exercise the native
//! binding pipeline end-to-end.
//!
//! SWAP POINT: replace the bodies in [`verify`] (and add the real crate deps) with leanSig / leanVM
//! calls. The `extern "C"` surface below is intentionally bytes-in / int-out so it does not change
//! when the real implementation and its serialization land.

use crate::verify::{leansphincs_tag, leanstark_tag, recursive_tag};

mod verify {
    use tiny_keccak::{Hasher, Keccak};

    pub const LEANSPHINCS_SCHEME: u8 = 0x10;
    pub const LEANSTARK_SCHEME: u8 = 0x11;

    fn keccak256(parts: &[&[u8]]) -> [u8; 32] {
        let mut hasher = Keccak::v256();
        for part in parts {
            hasher.update(part);
        }
        let mut out = [0u8; 32];
        hasher.finalize(&mut out);
        out
    }

    /// Placeholder leanSPHINCS "signature" over (data_hash, verification_key).
    pub fn leansphincs_tag(data_hash: &[u8], vk: &[u8]) -> [u8; 32] {
        keccak256(&[&[LEANSPHINCS_SCHEME], data_hash, vk])
    }

    /// Placeholder leanSTARK "proof" over (data_hash, verification_key).
    pub fn leanstark_tag(data_hash: &[u8], vk: &[u8]) -> [u8; 32] {
        keccak256(&[&[LEANSTARK_SCHEME], data_hash, vk])
    }

    /// Placeholder recursive-STARK "proof" over (deps_hash, aggregated_vk).
    pub fn recursive_tag(deps_hash: &[u8], aggregated_vk: &[u8]) -> [u8; 32] {
        keccak256(&[deps_hash, aggregated_vk])
    }
}

/// SAFETY: every pointer below must either be null or point to at least `len` readable bytes; the
/// 32-byte hash pointers must reference 32 readable bytes. The C# caller (NativeLeanProofVerifier)
/// guarantees this with pinned spans. Null pointers are treated as empty slices.
unsafe fn as_slice<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(ptr, len)
    }
}

/// ABI version; bump on any breaking change to the exported signatures.
#[no_mangle]
pub extern "C" fn nlean_abi_version() -> u32 {
    1
}

/// Returns 1 if the leanSPHINCS witness verifies for (data_hash, verification_key), else 0.
#[no_mangle]
pub unsafe extern "C" fn nlean_verify_leansphincs(
    data_hash: *const u8,
    vk: *const u8,
    witness: *const u8,
    witness_len: usize,
) -> i32 {
    let expected = leansphincs_tag(as_slice(data_hash, 32), as_slice(vk, 32));
    (as_slice(witness, witness_len) == expected) as i32
}

/// Returns 1 if the leanSTARK witness verifies for (data_hash, verification_key), else 0.
#[no_mangle]
pub unsafe extern "C" fn nlean_verify_leanstark(
    data_hash: *const u8,
    vk: *const u8,
    witness: *const u8,
    witness_len: usize,
) -> i32 {
    let expected = leanstark_tag(as_slice(data_hash, 32), as_slice(vk, 32));
    (as_slice(witness, witness_len) == expected) as i32
}

/// Returns 1 if the recursive proof verifies for (deps_hash, aggregated_vk), else 0.
#[no_mangle]
pub unsafe extern "C" fn nlean_verify_recursive(
    deps_hash: *const u8,
    aggregated_vk: *const u8,
    aggregated_vk_len: usize,
    proof: *const u8,
    proof_len: usize,
) -> i32 {
    let expected = recursive_tag(as_slice(deps_hash, 32), as_slice(aggregated_vk, aggregated_vk_len));
    (as_slice(proof, proof_len) == expected) as i32
}

/// Writes the 32-byte placeholder recursive proof for (deps_hash, aggregated_vk) into `out`.
/// Returns the number of bytes written, or -1 if `out` is null. (Prover-side shim; a real builder
/// would call leanVM here.)
#[no_mangle]
pub unsafe extern "C" fn nlean_prove_recursive(
    deps_hash: *const u8,
    aggregated_vk: *const u8,
    aggregated_vk_len: usize,
    out: *mut u8,
) -> i32 {
    if out.is_null() {
        return -1;
    }
    let proof = recursive_tag(as_slice(deps_hash, 32), as_slice(aggregated_vk, aggregated_vk_len));
    std::ptr::copy_nonoverlapping(proof.as_ptr(), out, 32);
    32
}
