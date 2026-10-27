// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
use nethermind_lean::{aggregated_vk, keccak};
fn main() {
    let directory = std::env::args().nth(1).expect("fixture output directory");
    if directory == "--sign-message" {
        let mut arguments = std::env::args().skip(2);
        let hash = arguments.next().expect("32-byte message as hex");
        assert_eq!(hash.len(), 64, "message must contain 64 hex digits");
        let mut message = [0; 32];
        for (index, byte) in message.iter_mut().enumerate() {
            *byte = u8::from_str_radix(&hash[index * 2..index * 2 + 2], 16).expect("hex message");
        }
        let output = arguments.next().expect("witness output file");
        assert!(arguments.next().is_none(), "unexpected fixture arguments");
        let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
        let signature = sphincs::sign(&sk, &message);
        std::fs::write(
            output,
            [pk.flatten().as_slice(), signature.to_bytes().as_slice()].concat(),
        )
        .unwrap();
        return;
    }
    std::fs::create_dir_all(&directory).unwrap();
    let save =
        |name: &str, bytes: &[u8]| std::fs::write(format!("{directory}/{name}"), bytes).unwrap();
    let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
    let message = [7; 32];
    let sig = sphincs::sign(&sk, &message);
    save("sphincs-key.bin", &pk.flatten());
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
    let key = aggregated_vk();
    save("aggregated-key.bin", &key);
    println!(
        "AGGREGATED_VK={}",
        key.iter().map(|b| format!("{b:02x}")).collect::<String>()
    );
}
