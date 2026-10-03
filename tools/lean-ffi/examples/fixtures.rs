// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
use nethermind_lean::{aggregated_vk, keccak, prove_stark};
fn main() {
    let directory = std::env::args().nth(1).expect("fixture output directory");
    std::fs::create_dir_all(&directory).unwrap();
    let save =
        |name: &str, bytes: &[u8]| std::fs::write(format!("{directory}/{name}"), bytes).unwrap();
    let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
    let message = [7; 32];
    let sig = sphincs::sign(&sk, &message);
    save("sphincs-key.bin", &keccak(&pk.flatten()));
    save("sphincs-message.bin", &message);
    save(
        "sphincs-signature.bin",
        &[pk.flatten().as_slice(), sig.to_bytes().as_slice()].concat(),
    );
    let mut multi = Vec::new();
    for index in 0u64..16 {
        let message = keccak(&index.to_le_bytes());
        let signature = sphincs::sign(&sk, &message);
        multi.extend_from_slice(&message);
        multi.extend_from_slice(&pk.flatten());
        multi.extend_from_slice(&signature.to_bytes());
    }
    save("sphincs-multi.bin", &multi);
    let mut multi_keys = Vec::new();
    for index in 0u8..16 {
        let mut seed = [42; 32];
        seed[0] = index;
        let (sk, pk) = sphincs::key_gen_from_seed(seed);
        let message = keccak(&[index]);
        multi_keys.extend_from_slice(&message);
        multi_keys.extend_from_slice(&pk.flatten());
        multi_keys.extend_from_slice(&sphincs::sign(&sk, &message).to_bytes());
    }
    save("sphincs-multi-keys.bin", &multi_keys);
    let source = "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = 7\n    p[GEN] = 9\n    return\n";
    let mut public_input = [0; 32];
    public_input[0] = 7;
    public_input[16] = 9;
    let (proof, key) = prove_stark(source, &public_input).unwrap();
    save("stark-key.bin", &key);
    save("stark-message.bin", &public_input);
    save("stark-proof.bin", &proof);
    let key = aggregated_vk();
    save("aggregated-key.bin", &key);
    println!(
        "AGGREGATED_VK={}",
        key.iter().map(|b| format!("{b:02x}")).collect::<String>()
    );
}
