// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use super::Verifier;
use anyhow::{Context, Result};
use pico_prism_vm::{
    configs::{
        config::{StarkGenericConfig, Val},
        stark_config::KoalaBearPoseidon2,
    },
    instances::{
        chiptype::recursion_chiptype::RecursionChipType, machine::combine::CombineMachine,
    },
    machine::{
        keys::BaseVerifyingKey,
        machine::MachineBehavior,
        proof::{BaseProof, MetaProof},
    },
    primitives::consts::RECURSION_NUM_PVS,
};
use serde::{Deserialize, Serialize};

// Serializable wrappers for MetaProof
#[derive(Serialize, Deserialize)]
struct SerializableKoalaBearMetaProof {
    proofs: Vec<BaseProof<KoalaBearPoseidon2>>,
    vks: Vec<BaseVerifyingKey<KoalaBearPoseidon2>>,
    pv_stream: Option<Vec<u8>>,
}

impl SerializableKoalaBearMetaProof {
    fn to_meta_proof(self) -> MetaProof<KoalaBearPoseidon2> {
        MetaProof::new(self.proofs.into(), self.vks.into(), self.pv_stream)
    }
}

struct KoalaBearCombineVerifier {
    machine: CombineMachine<KoalaBearPoseidon2, RecursionChipType<Val<KoalaBearPoseidon2>>>,
}

impl KoalaBearCombineVerifier {
    fn new() -> Self {
        let machine = CombineMachine::new(
            KoalaBearPoseidon2::new(),
            RecursionChipType::combine_chips(),
            RECURSION_NUM_PVS,
        );
        Self { machine }
    }

    fn verify(
        &self,
        proof: &MetaProof<KoalaBearPoseidon2>,
        riscv_vk: &BaseVerifyingKey<KoalaBearPoseidon2>,
    ) -> Result<bool> {
        self.machine
            .verify(proof, riscv_vk)
            .map(|_| true)
            .context("KoalaBear combine verification failed")
    }
}

pub struct PicoVerifier;

impl Verifier for PicoVerifier {
    fn verify(proof: &[u8], vk: &[u8]) -> Result<bool> {
        // Deserialize the KoalaBear proof
        let serializable_proof: SerializableKoalaBearMetaProof =
            bincode::deserialize(proof)
                .context("Failed to deserialize Pico proof")?;
        let proof = serializable_proof.to_meta_proof();

        // Deserialize KoalaBear verification key
        let riscv_vk: BaseVerifyingKey<KoalaBearPoseidon2> = bincode::deserialize(vk)
            .context("Failed to deserialize Pico verification key")?;

        // Create and run verifier
        let verifier = KoalaBearCombineVerifier::new();
        verifier.verify(&proof, &riscv_vk)
    }
}
