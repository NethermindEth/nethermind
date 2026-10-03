// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use nethermind_lean::{aggregated_vk, keccak, nlean_verify_recursive, prove_aggregate};
use std::time::Instant;

fn number(out: &mut Vec<u8>, value: usize) {
    out.extend_from_slice(&(value as u32).to_le_bytes());
}
fn blob(out: &mut Vec<u8>, value: &[u8]) {
    number(out, value.len());
    out.extend_from_slice(value);
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let count: usize = args.get(1).expect("claim count").parse().unwrap();
    let children = args.get(2).is_some_and(|mode| mode == "children");
    let direct = args.get(2).is_some_and(|mode| mode == "direct");
    assert!((1..=128).contains(&count));
    assert!(!direct || count <= 8);
    assert!(!children || count <= 16);
    let key = aggregated_vk();
    let mut deps = Vec::new();
    let mut witnesses = Vec::new();
    let mut signatures = Vec::new();
    for i in 0..count {
        let mut seed = [42; 32];
        seed[0] = i as u8;
        let (sk, pk) = sphincs::key_gen_from_seed(seed);
        let message = keccak(&(i as u64).to_le_bytes());
        let mut dep = [0; 96];
        dep[31] = 0x10;
        dep[32..64].copy_from_slice(&message);
        dep[64..].copy_from_slice(&keccak(&pk.flatten()));
        deps.push(dep);
        let signature = sphincs::sign(&sk, &message);
        witnesses.push([pk.flatten().as_slice(), signature.to_bytes().as_slice()].concat());
        signatures.push((pk, message, signature));
    }
    let mut prepared = Vec::new();
    if children {
        for (dep, witness) in deps.iter().zip(&witnesses) {
            let mut input = Vec::new();
            number(&mut input, 1);
            input.extend_from_slice(dep);
            blob(&mut input, witness);
            number(&mut input, 0);
            number(&mut input, 0);
            prepared.push(prove_aggregate(&keccak(dep), &input).unwrap());
        }
    }
    let mut input = Vec::new();
    if children {
        number(&mut input, 0);
        number(&mut input, prepared.len());
        for (dep, proof) in deps.iter().zip(&prepared) {
            number(&mut input, 1);
            input.extend_from_slice(dep);
            blob(&mut input, proof);
        }
    } else {
        number(&mut input, deps.len());
        for (dep, witness) in deps.iter().zip(&witnesses) {
            input.extend_from_slice(dep);
            blob(&mut input, witness);
        }
        number(&mut input, 0);
    }
    number(&mut input, 0);
    deps.sort();
    let hash = keccak(&deps.iter().flatten().copied().collect::<Vec<_>>());
    let started = Instant::now();
    let proof = if direct {
        let proof = rec_aggregation::aggregate(&[], vec![], signatures, &[], None, 1).unwrap();
        let mut envelope = b"NLR2".to_vec();
        number(&mut envelope, deps.len());
        for dep in &deps {
            envelope.extend_from_slice(dep);
        }
        blob(&mut envelope, &proof.to_bytes());
        number(&mut envelope, 0);
        envelope
    } else {
        prove_aggregate(&hash, &input).unwrap()
    };
    let proving = started.elapsed();
    let started = Instant::now();
    // These owned buffers stay valid for the complete C ABI call.
    let valid = unsafe {
        nlean_verify_recursive(
            hash.as_ptr(),
            key.as_ptr(),
            key.len(),
            proof.as_ptr(),
            proof.len(),
        )
    };
    assert_eq!(valid, 1);
    println!("mode,claims,proof_bytes,proving_ms,verification_ms");
    println!(
        "{},{count},{},{:.3},{:.3}",
        if direct {
            "direct"
        } else if children {
            "children"
        } else {
            "raw-binary-tree"
        },
        proof.len(),
        proving.as_secs_f64() * 1000.0,
        started.elapsed().as_secs_f64() * 1000.0
    );
}
