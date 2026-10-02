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
    let sig = sphincs::sign(&sk, &message).unwrap();
    save("sphincs-key.bin", &keccak(&pk.flatten()));
    save("sphincs-message.bin", &message);
    save(
        "sphincs-signature.bin",
        &[pk.flatten().as_slice(), sig.to_bytes().as_slice()].concat(),
    );
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
