// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use nethermind_lean::{keccak, prove_stark};
use std::io::{BufWriter, Write};
use std::time::Instant;

fn main() {
    let generation_started = Instant::now();
    let args: Vec<_> = std::env::args().collect();
    let directory = args.get(1).expect("output directory");
    let sphincs_count: u64 = args.get(2).map(|s| s.parse().unwrap()).unwrap_or(20000);
    let stark_count: u64 = args.get(3).map(|s| s.parse().unwrap()).unwrap_or(1200);
    std::fs::create_dir_all(&directory).unwrap();
    let save = |name: &str, bytes: &[u8]| {
        std::fs::write(format!("{directory}/{name}"), bytes).unwrap();
    };
    let (_, pk) = sphincs::key_gen_from_seed([42; 32]);
    save("sphincs-key.bin", &keccak(&pk.flatten()));
    let keys: Vec<_> = (0u8..16)
        .map(|index| {
            let mut seed = [42; 32];
            seed[0] = index;
            sphincs::key_gen_from_seed(seed)
        })
        .collect();
    let mut timings = String::from("scheme,index,generation_wall_ms,proof_bytes\n");
    let mut signatures =
        BufWriter::new(std::fs::File::create(format!("{directory}/sphincs.bin")).unwrap());
    signatures
        .write_all(&(sphincs_count as u32).to_le_bytes())
        .unwrap();
    for index in 0u64..sphincs_count {
        let mut identity = index.to_le_bytes().to_vec();
        identity.extend_from_slice(&(index % 16).to_le_bytes());
        identity.extend_from_slice(b"LeanBenchSphincs");
        let message = keccak(&identity);
        let started = Instant::now();
        let (sk, pk) = &keys[index as usize % keys.len()];
        let signature = sphincs::sign(sk, &message);
        let elapsed = started.elapsed().as_secs_f64() * 1000.0;
        let witness = [pk.flatten().as_slice(), signature.to_bytes().as_slice()].concat();
        signatures.write_all(&message).unwrap();
        signatures.write_all(&witness).unwrap();
        timings.push_str(&format!("sphincs,{index},{elapsed},{}\n", witness.len()));
    }
    let mut starks =
        BufWriter::new(std::fs::File::create(format!("{directory}/starks.bin")).unwrap());
    starks
        .write_all(&(stark_count as u32).to_le_bytes())
        .unwrap();
    for index in 0..stark_count {
        let value = index + 7;
        let source = format!(
            "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = {value}\n    p[GEN] = 9\n    return\n"
        );
        let mut input = [0; 32];
        input[..8].copy_from_slice(&(value as u64).to_le_bytes());
        input[16] = 9;
        let started = Instant::now();
        let (proof, key) = prove_stark(&source, &input).unwrap();
        let elapsed = started.elapsed().as_secs_f64() * 1000.0;
        starks.write_all(&input).unwrap();
        starks.write_all(&key).unwrap();
        starks
            .write_all(&(proof.len() as u32).to_le_bytes())
            .unwrap();
        starks.write_all(&proof).unwrap();
        timings.push_str(&format!("stark,{index},{elapsed},{}\n", proof.len()));
    }
    save("fixture-generation.csv", timings.as_bytes());
    save(
        "fixture-generation.json",
        format!(
            "{{\"wallSeconds\":{},\"sphincsClaims\":{},\"sphincsKeys\":16,\"starkProofs\":{}}}",
            generation_started.elapsed().as_secs_f64(),
            sphincs_count,
            stark_count
        )
        .as_bytes(),
    );
    println!(
        "Generated {sphincs_count} distinct SPHINCS claims and {stark_count} genuine leanSTARK proofs."
    );
}
