// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use super::Verifier;
use anyhow::Result;
use std::cell::RefCell;
use std::collections::BTreeMap;
use std::io::Read;

use bincode_airbender as bincode;
use cs::one_row_compiler::CompiledCircuitArtifact;
use full_statement_verifier::definitions::{
    OP_VERIFY_UNIFIED_RECURSION_LAYER_IN_UNIFIED_CIRCUIT,
    OP_VERIFY_UNROLLED_RECURSION_LAYER_IN_UNIFIED_CIRCUIT,
};
use prover::common_constants;
use prover::common_constants::TimestampScalar;
use prover::cs::utils::split_timestamp;
use prover::prover_stages::unrolled_prover::UnrolledModeProof;
use prover::prover_stages::Proof;
use verifier_common::field::Mersenne31Field;
use verifier_common::proof_flattener;
use verifier_common::prover::definitions::MerkleTreeCap;

pub struct AirbenderVerifier;

const DEFAULT_SETUP_BIN: &[u8] = include_bytes!("../../assets/airbender/recursion_unified_setup.bin");
const DEFAULT_LAYOUT_BIN: &[u8] = include_bytes!("../../assets/airbender/recursion_unified_layouts.bin");

pub fn init_defaults() -> Result<()> {
    init_with(DEFAULT_SETUP_BIN, DEFAULT_LAYOUT_BIN)
}

impl Verifier for AirbenderVerifier {
    fn verify(proof: &[u8], _vk: &[u8]) -> Result<bool> {
        let proof_handle = deserialize_proof_bytes(proof)?;

        if CONTEXT.with(|slot| slot.borrow().is_none()) {
            init_defaults()?;
        }

        CONTEXT.with(|slot| {
            let context = slot.borrow();
            let Some(context) = context.as_ref() else {
                return Err(anyhow::anyhow!(
                    "verifier not initialized (call init_defaults or init_with)"
                ));
            };

            match verify_proof_in_unified_layer(
                &proof_handle.proof,
                &context.setup,
                &context.layout,
                false,
            ) {
                Ok(_result) => Ok(true),
                Err(()) => Err(anyhow::anyhow!("Failed to verify proof")),
            }
        })
    }
}

// ================= Helpers for Proof deserialization and validation =================

pub struct ProofHandle {
    proof: UnrolledProgramProof,
}

pub fn deserialize_proof_bytes(proof_bytes: &[u8]) -> Result<ProofHandle> {
    let mut decoder = flate2::read::GzDecoder::new(proof_bytes);
    let mut decompressed = Vec::new();
    decoder
        .read_to_end(&mut decompressed)
        .map_err(|err| anyhow::anyhow!("gzip decode failed: {err}"))?;

    let (proof, _bytes_read): (UnrolledProgramProof, usize) =
        bincode::serde::decode_from_slice(&decompressed, bincode::config::standard())
            .map_err(|err| anyhow::anyhow!("bincode decode failed: {err}"))?;
    Ok(ProofHandle { proof })
}

pub fn verify_proof_in_unified_layer(
    proof: &UnrolledProgramProof,
    setup: &UnrolledProgramSetup,
    compiled_layouts: &CompiledCircuitsSet,
    input_is_unrolled: bool,
) -> Result<[u32; 16], ()> {
    let responses = flatten_proof_into_responses_for_unified_recursion(
        proof,
        setup,
        compiled_layouts,
        input_is_unrolled,
    );
    let result = std::thread::Builder::new()
        .name("verifier thread".to_string())
        .stack_size(1 << 27)
        .spawn(move || {
            let it = responses.into_iter();
            prover::nd_source_std::set_iterator(it);

            full_statement_verifier::unified_circuit_statement::
                  verify_unrolled_or_unified_circuit_recursion_layer()
        })
        .expect("must spawn verifier thread")
        .join();

    result.map_err(|_| ())
}

#[derive(Clone, Debug, Hash, serde::Serialize, serde::Deserialize)]
pub struct UnrolledProgramProof {
    pub final_pc: u32,
    pub final_timestamp: TimestampScalar,
    pub circuit_families_proofs: BTreeMap<u8, Vec<UnrolledModeProof>>,
    pub inits_and_teardowns_proofs: Vec<UnrolledModeProof>,
    pub delegation_proofs: BTreeMap<u32, Vec<Proof>>,
    pub register_final_values: [FinalRegisterValue; 32],
    pub recursion_chain_preimage: Option<[u32; 16]>,
    pub recursion_chain_hash: Option<[u32; 8]>,
}

impl UnrolledProgramProof {
    pub fn flatten_into_responses(
        &self,
        allowed_delegation_circuits: &[u32],
        compiled_layouts: &CompiledCircuitsSet,
    ) -> Vec<u32> {
        let mut responses = Vec::with_capacity(32 + 32 * 2);

        assert_eq!(self.register_final_values.len(), 32);
        for final_values in self.register_final_values.iter() {
            responses.push(final_values.value);
            let (low, high) = split_timestamp(final_values.last_access_timestamp);
            responses.push(low);
            responses.push(high);
        }

        responses.push(self.final_pc);
        let (low, high) = split_timestamp(self.final_timestamp);
        responses.push(low);
        responses.push(high);

        for (family, proofs) in self.circuit_families_proofs.iter() {
            responses.push(proofs.len() as u32);
            for proof in proofs.iter() {
                let Some(artifact) = compiled_layouts.compiled_circuit_families.get(family) else {
                    panic!(
                        "Proofs file has a proof for circuit type {}, but there is no matching compiled circuit in the set",
                        family
                    );
                };
                let flattened = proof_flattener::flatten_full_unrolled_proof(proof, artifact);
                responses.extend(flattened);
            }
        }

        if let Some(compiled_inits_and_teardowns) =
            compiled_layouts.compiled_inits_and_teardowns.as_ref()
        {
            responses.push(self.inits_and_teardowns_proofs.len() as u32);
            for proof in self.inits_and_teardowns_proofs.iter() {
                let flattened = proof_flattener::flatten_full_unrolled_proof(
                    proof,
                    compiled_inits_and_teardowns,
                );
                responses.extend(flattened);
            }
        } else {
            responses.push(0u32);
        }

        for delegation_type in allowed_delegation_circuits.iter() {
            if *delegation_type == common_constants::NON_DETERMINISM_CSR {
                continue;
            }
            if let Some(proofs) = self.delegation_proofs.get(delegation_type) {
                responses.push(proofs.len() as u32);
                for proof in proofs.iter() {
                    let flattened = proof_flattener::flatten_full_proof(proof, 0);
                    responses.extend(flattened);
                }
            } else {
                responses.push(0);
            }
        }

        if let Some(preimage) = self.recursion_chain_preimage {
            responses.extend(preimage);
        }

        responses
    }
}

fn flatten_proof_into_responses_for_unified_recursion(
    proof: &UnrolledProgramProof,
    setup: &UnrolledProgramSetup,
    compiled_layouts: &CompiledCircuitsSet,
    input_is_unrolled: bool,
) -> Vec<u32> {
    let mut responses = vec![];
    let op = if input_is_unrolled {
        assert!(setup.circuit_families_setups.len() > 1);
        assert!(!proof.inits_and_teardowns_proofs.is_empty());

        OP_VERIFY_UNROLLED_RECURSION_LAYER_IN_UNIFIED_CIRCUIT
    } else {
        assert_eq!(setup.circuit_families_setups.len(), 1);
        assert!(setup
            .circuit_families_setups
            .contains_key(&common_constants::REDUCED_MACHINE_CIRCUIT_FAMILY_IDX));

        assert_eq!(proof.circuit_families_proofs.len(), 1);
        assert!(proof.inits_and_teardowns_proofs.is_empty());
        assert!(!proof.circuit_families_proofs
            [&common_constants::REDUCED_MACHINE_CIRCUIT_FAMILY_IDX]
            .is_empty());

        OP_VERIFY_UNIFIED_RECURSION_LAYER_IN_UNIFIED_CIRCUIT
    };
    responses.push(op);
    if input_is_unrolled {
        responses.extend(setup.flatten_for_recursion());
    } else {
        responses.extend(setup.flatten_unified_for_recursion());
    }
    responses.extend(proof.flatten_into_responses(
        &[
            common_constants::delegation_types::blake2s_with_control::BLAKE2S_DELEGATION_CSR_REGISTER,
        ],
        compiled_layouts,
    ));

    responses
}

#[derive(Clone, Debug, Hash, serde::Serialize, serde::Deserialize)]
pub struct UnrolledProgramSetup {
    pub expected_final_pc: u32,
    pub binary_hash: [u8; 32],
    pub circuit_families_setups: BTreeMap<u8, [MerkleTreeCap<CAP_SIZE>; NUM_COSETS]>,
    pub inits_and_teardowns_setup: [MerkleTreeCap<CAP_SIZE>; NUM_COSETS],
    pub end_params: [u32; 8],
}

impl UnrolledProgramSetup {
    pub fn flatten_for_recursion(&self) -> Vec<u32> {
        let mut result = vec![];
        for (_, caps) in self.circuit_families_setups.iter() {
            result.extend_from_slice(MerkleTreeCap::flatten(caps));
        }
        result.extend_from_slice(MerkleTreeCap::flatten(&self.inits_and_teardowns_setup));

        result
    }

    pub fn flatten_unified_for_recursion(&self) -> Vec<u32> {
        assert_eq!(self.circuit_families_setups.len(), 1);
        let mut result = vec![];
        for (_, caps) in self.circuit_families_setups.iter() {
            result.extend_from_slice(MerkleTreeCap::flatten(caps));
        }

        result
    }
}

struct VerifierContext {
    setup: UnrolledProgramSetup,
    layout: CompiledCircuitsSet,
}

impl VerifierContext {
    fn parse(setup_bin: &[u8], layout_bin: &[u8]) -> Result<Self, String> {
        let (setup, _): (UnrolledProgramSetup, usize) =
            bincode::serde::decode_from_slice(setup_bin, bincode::config::standard())
                .map_err(|err| format!("failed to parse setup.bin: {err}"))?;
        let (layout, _): (CompiledCircuitsSet, usize) =
            bincode::serde::decode_from_slice(layout_bin, bincode::config::standard())
                .map_err(|err| format!("failed to parse layouts.bin: {err}"))?;
        Ok(Self { setup, layout })
    }

    fn set_global(self) {
        CONTEXT.with(|slot| {
            slot.borrow_mut().replace(self);
        });
    }
}

thread_local! {
    static CONTEXT: RefCell<Option<VerifierContext>> = const { RefCell::new(None) };
}

pub fn init_with(setup_bin: &[u8], layout_bin: &[u8]) -> Result<()> {
    let context =
        VerifierContext::parse(setup_bin, layout_bin).map_err(|err| anyhow::anyhow!(err))?;
    context.set_global();
    Ok(())
}

const CAP_SIZE: usize = 64;
const NUM_COSETS: usize = 2;

#[derive(Clone, Debug, Hash, serde::Serialize, serde::Deserialize)]
pub struct CompiledCircuitsSet {
    pub compiled_circuit_families: BTreeMap<u8, CompiledCircuitArtifact<Mersenne31Field>>,
    pub compiled_inits_and_teardowns: Option<CompiledCircuitArtifact<Mersenne31Field>>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
pub struct FinalRegisterValue {
    pub value: u32,
    pub last_access_timestamp: TimestampScalar,
}
