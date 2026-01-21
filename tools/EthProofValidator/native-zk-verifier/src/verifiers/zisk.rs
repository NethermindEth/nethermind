// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use super::Verifier;
use anyhow::Result;

pub struct ZiskVerifier;

impl Verifier for ZiskVerifier {
    fn verify(proof: &[u8], vk: &[u8]) -> Result<bool> {
        match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            proofman_verifier::verify(proof, vk)
        })) {
            Ok(true) => Ok(true),
            _ => Ok(proofman_verifier_fallback::verify(proof, vk)),
        }
    }
}
