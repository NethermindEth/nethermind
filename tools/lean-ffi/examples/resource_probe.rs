// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use nethermind_lean::{
    MAX_BYTES, MAX_INPUT_BYTES, aggregated_vk, keccak, nlean_verify_recursive, prove_aggregate,
    prove_stark,
};
use rec_aggregation::eip8288_mixed;
use std::io::Read;
use std::path::Path;
use std::time::{Duration, Instant};

type Dependency = [u8; 96];

fn number(out: &mut Vec<u8>, value: usize) {
    out.extend_from_slice(&u32::try_from(value).unwrap().to_le_bytes());
}
fn blob(out: &mut Vec<u8>, value: &[u8]) {
    number(out, value.len());
    out.extend_from_slice(value);
}
fn dependency(scheme: u8, data: [u8; 32], key: [u8; 32]) -> Dependency {
    let mut dep = [0; 96];
    dep[31] = scheme;
    dep[32..64].copy_from_slice(&data);
    dep[64..].copy_from_slice(&key);
    dep
}
fn raw_input(dep: &Dependency, witness: &[u8]) -> Vec<u8> {
    let mut input = Vec::new();
    number(&mut input, 1);
    input.extend_from_slice(dep);
    blob(&mut input, witness);
    number(&mut input, 0);
    number(&mut input, 0);
    input
}
fn child_input(deps: &[Dependency], parents: &[Vec<u8>]) -> Vec<u8> {
    assert_eq!(deps.len(), parents.len());
    let mut input = Vec::new();
    number(&mut input, 0);
    number(&mut input, parents.len());
    for (dep, parent) in deps.iter().zip(parents) {
        number(&mut input, 1);
        input.extend_from_slice(dep);
        blob(&mut input, parent);
    }
    number(&mut input, 0);
    assert!(input.len() <= MAX_INPUT_BYTES);
    input
}
fn verify(hash: &[u8; 32], key: &[u8; 32], proof: &[u8]) {
    assert!(proof.len() <= MAX_BYTES);
    // Owned buffers remain valid throughout the C ABI call.
    let valid = unsafe {
        nlean_verify_recursive(
            hash.as_ptr(),
            key.as_ptr(),
            key.len(),
            proof.as_ptr(),
            proof.len(),
        )
    };
    assert_eq!(valid, 1, "native recursive verification failed");
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}
fn bounded_read(path: &Path, max: usize) -> Vec<u8> {
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .unwrap()
        .take((max + 1) as u64)
        .read_to_end(&mut bytes)
        .unwrap();
    assert!(bytes.len() <= max, "{} exceeds its bound", path.display());
    bytes
}
fn report(
    mode: &str,
    count: usize,
    generic_count: usize,
    input_bytes: usize,
    proof: &[u8],
    proving: Duration,
    verification: Duration,
    fixture_generation: Duration,
    child_preparation: Duration,
    total: Duration,
    key: &[u8; 32],
) {
    println!(
        "mode,claims,proof_bytes,proving_ms,verification_ms,fixture_generation_ms,child_preparation_ms,total_ms,sphincs_claims,generic_claims,input_bytes,profile_key"
    );
    println!(
        "{mode},{count},{},{:.3},{:.3},{:.3},{:.3},{:.3},{},{generic_count},{input_bytes},{}",
        proof.len(),
        proving.as_secs_f64() * 1000.0,
        verification.as_secs_f64() * 1000.0,
        fixture_generation.as_secs_f64() * 1000.0,
        child_preparation.as_secs_f64() * 1000.0,
        total.as_secs_f64() * 1000.0,
        count - generic_count,
        hex(key),
    );
}
fn mixed_prepare(directory: &Path) {
    let total_started = Instant::now();
    let key = aggregated_vk();
    let fixture_started = Instant::now();
    let (sk, pk) = sphincs::key_gen_from_seed([117; 32]);
    let message = keccak(b"LeanBench mixed resource S1");
    let signature = sphincs::sign(&sk, &message);
    let sph_dep = dependency(0x10, message, keccak(&pk.flatten()));
    let sph_witness = [pk.flatten().as_slice(), signature.to_bytes().as_slice()].concat();
    let mut data = [0; 32];
    data[0] = 7;
    data[16] = 9;
    let source = "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = 7\n    p[GEN] = 9\n    return\n";
    let (generic_witness, generic_key) = prove_stark(source, &data).unwrap();
    let generic_dep = dependency(0x11, data, generic_key);
    let fixture_generation = fixture_started.elapsed();
    let preparation_started = Instant::now();
    let parents: Vec<_> = [(sph_dep, sph_witness), (generic_dep, generic_witness)]
        .iter()
        .map(|(dep, witness)| {
            let hash = keccak(dep);
            let proof = prove_aggregate(&hash, &raw_input(dep, witness)).unwrap();
            verify(&hash, &key, &proof);
            proof
        })
        .collect();
    let preparation = preparation_started.elapsed();
    let mut deps = [sph_dep, generic_dep];
    deps.sort();
    let dep_bytes: Vec<_> = deps.iter().flatten().copied().collect();
    let hash = keccak(&dep_bytes);
    let input = child_input(&[sph_dep, generic_dep], &parents);
    std::fs::create_dir_all(directory).unwrap();
    for (name, bytes) in [
        ("profile-key.bin", key.as_slice()),
        ("dependencies.bin", dep_bytes.as_slice()),
        ("dependency-hash.bin", hash.as_slice()),
        ("input.bin", input.as_slice()),
        ("sphincs-parent.bin", parents[0].as_slice()),
        ("generic-parent.bin", parents[1].as_slice()),
    ] {
        std::fs::write(directory.join(name), bytes).unwrap();
    }
    println!(
        "mode,claims,sphincs_claims,generic_claims,input_bytes,fixture_generation_ms,child_preparation_ms,total_ms,profile_key,input_keccak"
    );
    println!(
        "mixed-prepare,2,1,1,{},{:.3},{:.3},{:.3},{},{}",
        input.len(),
        fixture_generation.as_secs_f64() * 1000.0,
        preparation.as_secs_f64() * 1000.0,
        total_started.elapsed().as_secs_f64() * 1000.0,
        hex(&key),
        hex(&keccak(&input)),
    );
}
fn mixed_merge(directory: &Path) {
    let total_started = Instant::now();
    let key = aggregated_vk();
    assert_eq!(bounded_read(&directory.join("profile-key.bin"), 32), key);
    let dep_bytes = bounded_read(&directory.join("dependencies.bin"), 192);
    assert_eq!(dep_bytes.len(), 192);
    let deps: Vec<Dependency> = dep_bytes
        .chunks_exact(96)
        .map(|dep| dep.try_into().unwrap())
        .collect();
    assert!(deps[0] < deps[1]);
    assert!(deps.iter().all(|dep| dep[..31] == [0; 31]));
    assert_eq!(deps.iter().filter(|dep| dep[31] == 0x10).count(), 1);
    assert_eq!(deps.iter().filter(|dep| dep[31] == 0x11).count(), 1);
    let hash = keccak(&dep_bytes);
    assert_eq!(
        bounded_read(&directory.join("dependency-hash.bin"), 32),
        hash
    );
    let parents = [
        bounded_read(&directory.join("sphincs-parent.bin"), MAX_BYTES),
        bounded_read(&directory.join("generic-parent.bin"), MAX_BYTES),
    ];
    let sph_dep = deps.iter().find(|dep| dep[31] == 0x10).unwrap();
    let generic_dep = deps.iter().find(|dep| dep[31] == 0x11).unwrap();
    let input = child_input(&[*sph_dep, *generic_dep], &parents);
    assert_eq!(
        bounded_read(&directory.join("input.bin"), MAX_INPUT_BYTES),
        input
    );
    let started = Instant::now();
    let proof = prove_aggregate(&hash, &input).unwrap();
    let proving = started.elapsed();
    let started = Instant::now();
    verify(&hash, &key, &proof);
    let verification = started.elapsed();
    std::fs::write(directory.join("mixed-root.bin"), &proof).unwrap();
    report(
        "mixed-merge",
        2,
        1,
        input.len(),
        &proof,
        proving,
        verification,
        Duration::ZERO,
        Duration::ZERO,
        total_started.elapsed(),
        &key,
    );
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    if matches!(
        args.get(1).map(String::as_str),
        Some("mixed-prepare" | "mixed-merge")
    ) {
        assert_eq!(
            args.len(),
            3,
            "usage: resource_probe mixed-prepare|mixed-merge DIR"
        );
        let directory = Path::new(&args[2]);
        if args[1] == "mixed-prepare" {
            mixed_prepare(directory);
        } else {
            mixed_merge(directory);
        }
        return;
    }
    let count: usize = args.get(1).expect("claim count").parse().unwrap();
    let mode = args.get(2).map(String::as_str).unwrap_or("raw-binary-tree");
    assert!(matches!(mode, "raw-binary-tree" | "direct" | "children"));
    let children = mode == "children";
    let direct = mode == "direct";
    assert!((1..=128).contains(&count));
    assert!(!direct || count <= 4);
    assert!(!children || count <= 16);
    let total_started = Instant::now();
    let fixture_started = Instant::now();
    let key = aggregated_vk();
    let mut deps = Vec::new();
    let mut witnesses = Vec::new();
    let mut signatures = Vec::new();
    for i in 0..count {
        let mut seed = [42; 32];
        seed[0] = i as u8;
        let (sk, pk) = sphincs::key_gen_from_seed(seed);
        let message = keccak(&(i as u64).to_le_bytes());
        let dep = dependency(0x10, message, keccak(&pk.flatten()));
        deps.push(dep);
        let signature = sphincs::sign(&sk, &message);
        witnesses.push([pk.flatten().as_slice(), signature.to_bytes().as_slice()].concat());
        signatures.push((pk, message, signature));
    }
    let fixture_generation = fixture_started.elapsed();
    let preparation_started = Instant::now();
    let mut prepared = Vec::new();
    if children {
        for (dep, witness) in deps.iter().zip(&witnesses) {
            prepared.push(prove_aggregate(&keccak(dep), &raw_input(dep, witness)).unwrap());
        }
    }
    let child_preparation = preparation_started.elapsed();
    let input = if children {
        child_input(&deps, &prepared)
    } else {
        let mut input = Vec::new();
        number(&mut input, deps.len());
        for (dep, witness) in deps.iter().zip(&witnesses) {
            input.extend_from_slice(dep);
            blob(&mut input, witness);
        }
        number(&mut input, 0);
        number(&mut input, 0);
        input
    };
    deps.sort();
    let hash = keccak(&deps.iter().flatten().copied().collect::<Vec<_>>());
    let started = Instant::now();
    let proof = if direct {
        let proof = eip8288_mixed::aggregate_mixed(&[], &signatures, &[], &deps, 1).unwrap();
        let mut envelope = b"NLR3".to_vec();
        number(&mut envelope, deps.len());
        for dep in &deps {
            envelope.extend_from_slice(dep);
        }
        blob(&mut envelope, &proof.to_bytes_without_deps());
        envelope
    } else {
        prove_aggregate(&hash, &input).unwrap()
    };
    let proving = started.elapsed();
    let started = Instant::now();
    verify(&hash, &key, &proof);
    let verification = started.elapsed();
    report(
        mode,
        count,
        0,
        input.len(),
        &proof,
        proving,
        verification,
        fixture_generation,
        child_preparation,
        total_started.elapsed(),
        &key,
    );
}
