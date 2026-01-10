// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use std::slice;
use std::panic;

mod verifiers;
use verifiers::{ Verifier, VerifierType, openvm::OpenVmVerifier, pico::PicoVerifier, sp1_hypercube::Sp1HypercubeVerifier, zisk::ZiskVerifier };

#[no_mangle]
pub extern "C" fn verify(
    zk_type: u32,
    proof_ptr: *const u8,
    proof_len: usize,
    vk_ptr: *const u8,
    vk_len: usize,
) -> i32 {
    // Check for null pointers from C# safety
    if proof_ptr.is_null() || vk_ptr.is_null() {
        return -1;
    }

    // Wrap the raw pointers in Rust slices (zero-copy)
    let proof = unsafe { slice::from_raw_parts(proof_ptr, proof_len) };
    let vk = unsafe { slice::from_raw_parts(vk_ptr, vk_len) };

    let verifier_type = match VerifierType::try_from(zk_type) {
        Ok(t) => t,
        Err(_) => return -1,
    };

    // Catch panics from the underlying verifier libraries
    let panic_result = panic::catch_unwind(|| {
        match verifier_type {
            VerifierType::Zisk => ZiskVerifier::verify(proof, vk),
            VerifierType::OpenVm => OpenVmVerifier::verify(proof, vk),
            VerifierType::Pico => PicoVerifier::verify(proof, vk),
            VerifierType::Sp1Hypercube => Sp1HypercubeVerifier::verify(proof, vk),
        }
    });

    match panic_result {
        Ok(verification_result) => match verification_result {
            Ok(true) => 1,
            Ok(false) => 0,
            Err(_) => -1,
        },
        Err(_) => -1,
    }
}

