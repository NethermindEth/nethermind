// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use super::Verifier;
use anyhow::{Result};
use verify_stark::{verify_vm_stark_proof, vk::VmStarkVerifyingKey};

pub struct OpenVmVerifier;

impl Verifier for OpenVmVerifier {
    fn verify(proof: &[u8], vk: &[u8]) -> Result<bool> {
        // Deserialize the verification key from bitcode bytes
        let vk: VmStarkVerifyingKey = match bitcode::deserialize(vk) {
            Ok(vk) => vk,
            Err(e) => {
                println!("Failed to deserialize OpenVM verification key: {:?}", e);
                return Err(anyhow::anyhow!("Failed to deserialize OpenVM verification key: {:?}", e));
            }
        };

        // Verify the proof using the OpenVM verify-stark library
        match verify_vm_stark_proof(&vk, proof) {
            Ok(()) => Ok(true),
            Err(e) => {
                println!("OpenVM verification failed: {:?}", e);
                return Err(anyhow::anyhow!("OpenVM verification failed: {:?}", e));
            }
        }
    }
}
