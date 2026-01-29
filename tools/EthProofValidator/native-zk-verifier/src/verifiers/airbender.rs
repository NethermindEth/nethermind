// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use super::Verifier;
use anyhow::Result;
use std::cell::RefCell;
use std::collections::BTreeMap;
use std::io::Read;
use base64::prelude::*;

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

const SETUP_DATA_BASE64: &str = "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AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/EM3QF38y3j23vy8Uxuj/AJcJ6f8ujaWVvwiW1c7/OVK8ar8IxY4Rg==";
const LAYOUT_DATA_BASE64: &str = "AYAHAQgBCQEKAQsACwgGAAEJAAAyAAAzARAAADQAADUAADYBEAAANwAAOAAAOQAAOgAAOwAAPAAAPQAAPgAAPwAAQAAAQQAAQgAAQwAARAAARQAARggAAAsAAAwAAA0AAA4AAA8AABAAABEAABIIAQEBASUAAQEBASYAAQP8AAAIAAAuAQEG/P7//38BIfz///9/AQMBAQf8/v//fwEi/P7//38ALvwAAAgAAQP8AAAIAAAvAQEL/P7//38BIfz+//9/AQMBAQz8/v//fwEi/P7//38AL/wAAAgAAQP8AAAIAAAwAQES/P7//38BIfz9//9/AQMBARP8/v//fwEi/P7//38AMPwAAAgACBMfRyNqAQABAgEEAQMAAAAKAQYBCAEAAQEPARABCwENAQECARYBFwESARQBGQEBGwEcAR0BAwABIwElAQEeAR8BIQEKAQAAAAEAAgEDAQUBAAAAAAAGAAAnAAAAAQEBAgEGARAEAAQMBAABAQQBHAEEBQQgBDAGSAFMAVABVAFYAQFcAXQBYANsAXABeAEMfHABAQATABMB/P7//38AEwABAQAUABQB/P7//38AFAABAQAVABUB/P7//38AFQABAQAWABYB/P7//38AFgABAQAXABcB/P7//38AFwABAQAYABgB/P7//38AGAABAQAZABkB/P7//38AGQABAQAaABoB/P7//38AGgABAQAbABsB/P7//38AGwABAQAcABwB/P7//38AHAABAQAdAB0B/P7//38AHQABAQAeAB4B/P7//38AHgABAQAfAB8B/P7//38AHwABAQAgACAB/P7//38AIAABAQAhACEB/P7//38AIQABAQAiACIB/P7//38AIgABAQAjACMB/P7//38AIwABAQAkACQB/P7//38AJAABAQAlACUB/P7//38AJQABAQAmACYB/P7//38AJgABAQAnACcB/P7//38AJwABAQAoACgB/P7//38AKAABAQApACkB/P7//38AKQABAQAqACoB/P7//38AKgABAQArACsB/P7//38AKwABAQAsACwB/P7//38ALAABAQAtAC0B/P7//38ALQABAQAuAC4B/P7//38ALgABAQAvAC8B/P7//38ALwABAQAwADAB/P7//38AMAABAQAxADEB/P7//38AMQABAQEeAR4B/P7//38BHgAC/P7//38AAwAUAQAUAQ0CAQAD/P7//38ARwAC/P7//38ABAAUAQAUAQ4CAQAE/P7//38ANAABAQAlAR8B/AMA/38AJQABAQBKAR8CAQAl/AMA/38ASvz+//9/Afz+//9/ACUBHwP8+///fwAl/P7//38ASAEBHwQCAQAlACYBACYBIAH8///+fwAmAAIBACUASwEASwEgAgEAJvz///5/AEv8/v//fwL8/v//fwAlACb8/v//fwAmASADAQAl/P7//38ASQEBIAABAQBOAFAB/P7//38AUgABAQAZAFIAAAIBAA0AUPz+//9/AEgAUAIBAEj8/v//fwBTAAIBAA4AUPz+//9/AEkAUAIBAEn8/v//fwBUAAEBACMAVQIBAE/8/v//fwBWAAH8/v//fwAjAFADAQBQAQBR/P7//38AVwAC/P7//38AIgEIAQAiAR8C/P7//38AWgEBCAAC/P7//38AIgEJAQAiASAC/P7//38AWwEBCQABAQAbAE4AAAT8AAABAAA0AQgCADQBCQEARwEI/AAAAQAARwEJAfz+//9/AFwACfwAAAEAACIANAEAIgBHAQAiAQj8AAABAAAiAQkBACMAXPz///5/ACQANPz+//9/ACQARwEAJAEI/AAAAQAAJAEJAvz+//9/AF38///+fwBeAAL8/v//fwALAB4BAB4AXQAAAvz+//9/AAwAHgEAHgBeAAAB/P7//38AHgAnAQEAHgAB/P7//38AHQBOAgEAHfz+//9/AF8AAQEAHQBOAfz+//9/AGAAAgEACwBf/P7//38AXwEQAAACAQAMAF/8/v//fwBfAREAAAIBAAsAYPz+//9/AGABEAAAAgEADABg/P7//38AYAERAAAC/P7//38AAAAdAQAdARACAQAA/P7//38BEAAB/P7//38AHQERAQEBEQACAQAhAE4BACEATwAAAfz+//9/ACEAUAEBACEAAgEACwAh/P7//38AIQEXAAACAQAMACH8/v//fwAhARgAAAIBACEAR/z+//9/ACEBGQAAAgEAIQA0/P7//38AIQEaAAAB/P7//38AGgBOAQEAGgABAQAaAE8B/P7//38BGwABAQEJARsB/P7//38BHQABAQADARsB/P7//38BHAABAQAPAE8AAAEBABAATwAAFwEAAwAXAQADABsBAAMAHQEAAwAh/P7//38ACwAW/P7//38ACwAXAQALABn8/v//fwALABv8/v//fwALAB38/v//fwALAB4BAAsAIPz+//9/AAsAIQEADQAeAQAWAEcBABYBCAEAFwEfAQAZAEf8/v//fwAZAQgBABsAWgEAHQEIAQAgAEf8/v//fwAgAQgBACEBCAL7//8AHvz///5/ACoAFwEABAAXAQAEABsBAAQAHQEABAAh/P7//38ADAAW/P7//38ADAAXAQAMABn8/v//fwAMABv8/v//fwAMAB38/v//fwAMAB4BAAwAIPz+//9/AAwAIQEADgAeAQAWADQBABYBCQEAFwEgAQAZADT8/v//fwAZAQkBABsAWwEAHQEJAQAgADT8/v//fwAgAQkBACEBCQP7/38AHvz///5/ACcBACoAAwEAAwAZ/P7//38ADQAZAQAZAR8B/P///n8AKwADAQAEABn8/v//fwAOABkBABkBIAL8///+fwApAQArAAEBAAsAGQH8/v//fwBhAAEBAAwAGQH8/v//fwBiAAIBAGEAYwEAYgBjAQEAKPz+//9/AgEAKABhAQAoAGIAAAcBAAMAGgEACwAbAQALACEBAAwAHQEADQAZAQAYAEwBAB8ATQH8/v//fwA3AAYBABgATQEAGQBOAQAaAE4BABsATgEAHQBOAQAhAE4CHwAf/P7//38AOAAHAQAYAE4BABkATwEAGgBPAQAbAE8BAB0ATwEAHwBOAQAhAE8B/P7//38AOQABAQAFABgHEQAZGQAaEQAbFwAdBwAfEgAh/P7//38AOgANAQAFABkBAAsAHQEADAAh/P//f38AGABM/AAAgAAAGAEICAAZACcQABkAKCAAGQAyQAAZADX8AAABAAAdAE/8AAAgAAAfACP8AAABAAAfAE4BAB8BCAH8/v//fwA7AAb8AACAAAAYAEf8//9/fwAYAE0BABkAUAEAHQBQAQAfAE8BACEAUAH8/v//fwA8AAUBABgATwEAGQBRAQAdAFEBAB8AUAEAIQBRAfz+//9/AD0AAQEABQAYBRYAGRgAHSUAHxcAIfz+//9/AD4ABfz//v9/ABgAMwEAGAEJ/AAAIAAAHwAj/AAAAQAAHwBOAQAfAQkB/P7//38APwADAQAYADT8//7/fwAYADYBAB8AUQH8/v//fwBAAAIBABgAUAEAHwBVAfz+//9/AEEAAQEABQAYAiUAH/z+//9/AEIABAEAGAAzAgAfACIBAB8AMgQAHwBOAfz+//9/AEMAAgEAGAA2AQAfAFgB/P7//38ARAACAQAYAFEBAB8AWQH8/v//fwBFAAEBAAUAGAIUAB/8/v//fwBGAA4BAAMAHAEACwAWAQALABcBAAsAHgEACwAgAQAPABoBABgATvsAAQAYAE8BABkAUQEAGwBIAQAfAFYBAB8AWAEAUABfAQBgAQ0B/P7//38AZAANAQAEABwBAAwAFgEADAAXAQAMAB4BAAwAIAEAEAAaAQAYAFD7AAEAGABRAQAbAEkBAB8AVwEAHwBZAQBRAF8BAGABDgH8/v//fwBlAAH8/v//fwACAGQCAQBk/P7//38AZgAB/P7//38AAgBlAgEAZfz+//9/AGcAAgEAAQAT/P7//38AEwEXAAABAQATARgAAAEBABUBFwAAAQEAFQEYAAACAQATAGb8/v//fwATARkAAAIBABMAZ/z+//9/ABMBGgAAAQEAFQEZAAABAQAVARoAAAwBABYASAEAFwBIAQAYAEgBABkAUwEAGgBIAQAbAE8BABwASAEAHQBIAQAeAEgBAB8ASAEAIABIAQAhAEgB/P7//38BIwAMAQAMABsBABYASQEAFwBJAQAYAEkBABkAVAEAGgBJAQAcAEkBAB0ASQEAHgBJAQAfAEkBACAASQEAIQBJAfz+//9/ASQABRP8/v//fwAGAQATAgAUBAAVCAAWEAAXIAAYQAAZgAAa+wABABv7AAIAHPsABAAd+wAIAB77ABAAH/sAIAAg+wBAACH7AIAAIvwAAAEAACP8AAACAAAkAAL8/v//fwAd/P7//38BDwEC/P7//38AIfz+//9/ARYBA/wAAAgAADH8/v//fwEhAQEl/Pv//38D/P7//38AMfz+//9/ASIBASYAAACNAAEfAQEgAgEhAwEiBAEeBQEKBgAABwABCAACCQADCgAECwAFDAAGDQEjDgEkDwElEAEmEQATEgAUEwAVFAAWFQAXFgAYFwAZGAAaGQAbGgAcGwAdHAAeHQAfHgAgHwAhIAAiIQAjIgAkIwEIJAEJJQENJgEOJwEQKAERKQEUKgEVKwEZLAEaLQEXLgEYLwBHMAA0MQBIMgBJMwBKNAAlNQBLNgAmNwBMOAAyOQAzOgBNOwA1PAA2PQALPgAMPwAnQABOQQBPQgBQQwBRRAAoRQANRgAORwApSABSSQBTSgBUSwBVTABWTQBXTgBYTwBZUABaUQBbUgBcUwBdVABeVQBfVgBgVwEPWAEWWQAPWgAQWwEbXAEdXQEcXgAqXwArYABhYQBiYgBjYwA3ZAA4ZQA5ZgA6ZwA7aAA8aQA9agA+awA/bABAbQBBbgBCbwBDcABEcQBFcgBGcwBkdABldQBmdgBndwBoeABpeQARegASewAsfAAtfQEAfgEBfwECgAEDgQEEggEFgwEGhAEHhQAuhgELhwEMiAAviQESigETiwAwjAAxAAEAEQASACwALQMALgAvADADAAAAAwADAAADAAAAAwADAAABADH8AAAQAPwAAIAAPgABAQEB/AEAAQD8AQABAPwBAAIA/AEAAwD8AQADAPwBAAMA/AEAAwD8AQADAPwBAAMA/AEAAwD8AQADAPwBAAMA/AEABAD8AQAFAPwBAAYA/AEABgD8gQAGAPyBAAYA/AEBBgD8AQEHAPwBARcA/AERFwD8AREXAPwBERcA/AERFwD8AREXAPwBERcA/AERFwD8AREXAPwBERcA/AERFwD8AREXAPwBERcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwD8ARFXAPwBEVcA/AERVwAA";

pub fn init_defaults() -> Result<()> {
    let setup = BASE64_STANDARD
        .decode(SETUP_DATA_BASE64)
        .map_err(|err| anyhow::anyhow!("base64 decode setup failed: {err}"))?;
    let layout = BASE64_STANDARD
        .decode(LAYOUT_DATA_BASE64)
        .map_err(|err| anyhow::anyhow!("base64 decode layout failed: {err}"))?;
    init_with(&setup, &layout)
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
