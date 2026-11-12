//! Shape and cost of fixed aggregation workloads, at log_inv_rate 1. `agg_bench sigs [n]` proves two disjoint
//! leanSPHINCS leaves of `n` signatures and their 2-children merge; `agg_bench generic` proves leaves of leanSTARK
//! dependencies (1 small, 1 of 2^11 instructions, 16 small, and 40 signatures + 4 small).

use lean_compiler::{compile, parse};
use leanvm_core::cpu::{Program, bytecode_table, prove};
use rec_aggregation::aggregation::unified_guest;
use rec_aggregation::{EthereumProof, RawGeneric, aggregate_with_stats, generic_verification_key, generic_witness};
use signatures::lean_sphincs;
use std::time::Instant;

const RATE: usize = 1;

type RawSphincs = (
    lean_sphincs::LeanSphincsPublicKey,
    lean_sphincs::Message,
    lean_sphincs::LeanSphincsSignature,
);

fn signers(n: usize, salt: u8) -> Vec<RawSphincs> {
    (0..n)
        .map(|i| {
            let mut seed = [salt; 32];
            seed[..8].copy_from_slice(&(i as u64).to_le_bytes());
            let (sk, pk) = lean_sphincs::key_gen_from_seed(seed);
            let mut message = [7; 32];
            message[0] = salt;
            message[31] = i as u8;
            (pk, message, lean_sphincs::sign(&sk, &message).expect("signs"))
        })
        .collect()
}

/// Publishes `keccak256` of a hinted 64-byte preimage.
const PREIMAGE: &str = "\
def main():
    pre = StackBuf(4)
    hint_witness(pre, \"pre\")
    c = StackBuf(2)
    keccak(pre[0:2], pre[2:4], c)
    p = 1
    p[1] = c[0]
    p[GEN] = c[1]
    return
";

/// Accepts any canonical public input after 900 multiply-adds: 2^11 instructions.
fn wide_source() -> String {
    let mut src = String::from("def main():\n    h = StackBuf(2)\n    hint_witness(h, \"pi\")\n    acc = h[0]\n");
    src += &"    acc = acc * h[1] + h[0]\n".repeat(900);
    src + "    p = 1\n    p[1] = h[0]\n    p[GEN] = h[1]\n    return\n"
}

fn cells(digest: &[u8; 32]) -> [primitives::field::F192; 2] {
    fiat_shamir::merkle::hash_to_scalars(digest)
}

fn dependency(program: &Program, witness: (&str, Vec<primitives::field::F192>), data_hash: [u8; 32]) -> RawGeneric {
    let table: Vec<u64> = bytecode_table(program.prog()).iter().map(|w| w.0).collect();
    let mut program = program.clone();
    program.set_witness(witness.0, vec![witness.1]);
    let (proof, _) = prove(&program, cells(&data_hash), RATE).expect("the dependency proves");
    let vk = generic_verification_key(&table).expect("within the cap");
    (data_hash, vk, generic_witness(&table, &proof).expect("a witness"))
}

fn preimage_deps(n: usize) -> Vec<RawGeneric> {
    let program = compile(&parse(PREIMAGE).expect("parse"));
    (0..n)
        .map(|i| {
            let pre = [i as u8 + 1; 64];
            let pre_cells = pre.chunks(16).map(|c| {
                primitives::field::F192::new(
                    u64::from_le_bytes(c[..8].try_into().unwrap()),
                    u64::from_le_bytes(c[8..].try_into().unwrap()),
                    0,
                )
            });
            dependency(&program, ("pre", pre_cells.collect()), primitives::hash::hash(&pre))
        })
        .collect()
}

fn report(name: &str, run: impl FnOnce() -> (EthereumProof, leanvm_core::cpu::Stats)) -> EthereumProof {
    let start = Instant::now();
    let (proof, stats) = run();
    let prove = start.elapsed().as_secs_f64();
    let start = Instant::now();
    proof.verify().expect("verifies");
    let verify = start.elapsed().as_secs_f64() * 1e3;
    println!(
        "{name}: cycles {}, prove {prove:.3} s, verify {verify:.2} ms, proof {} B, log_mem {}, committed 2^{:.2}",
        stats.cycles,
        proof.proof_bytes().len(),
        stats.log_mem,
        (stats.committed as f64).log2()
    );
    proof
}

fn main() {
    leanvm_core::init_prover();
    let mode = std::env::args().nth(1).unwrap_or_else(|| "sigs".into());
    rec_aggregation::warm_up();
    let guest = unified_guest();
    let vk: String = rec_aggregation::aggregated_vk().iter().map(|b| format!("{b:02x}")).collect();
    println!(
        "guest code_len {}, bytecode log {}, aggregated vk {vk}",
        guest.code_len(),
        guest.prog().len().trailing_zeros()
    );
    let leaf = |deps: Vec<RawGeneric>, sigs: Vec<RawSphincs>| {
        move || aggregate_with_stats(&[], deps, sigs, &[], None, RATE).expect("proves")
    };
    match mode.as_str() {
        "sigs" => {
            let n: usize = std::env::args().nth(2).map_or(40, |a| a.parse().expect("n"));
            let left = report(&format!("leaf {n} sigs"), leaf(vec![], signers(n, 1)));
            let right = report(&format!("leaf {n} sigs (2nd)"), leaf(vec![], signers(n, 2)));
            report("merge 2 children", || {
                aggregate_with_stats(&[left, right], vec![], vec![], &[], None, RATE).expect("proves")
            });
        }
        "generic" => {
            let wide = compile(&parse(&wide_source()).expect("parse"));
            let small = compile(&parse(PREIMAGE).expect("parse")).prog().len();
            println!("small program {small} instructions, wide program {}", wide.prog().len());
            let deps = preimage_deps(16);
            report("leaf 1 small generic", leaf(deps[..1].to_vec(), vec![]));
            let data_hash = [9; 32];
            let wide_dep = dependency(&wide, ("pi", cells(&data_hash).to_vec()), data_hash);
            report("leaf 1 generic of 2^11 instructions", leaf(vec![wide_dep], vec![]));
            report("leaf 16 small generic", leaf(deps.clone(), vec![]));
            report(
                "leaf 40 sigs + 4 small generic",
                leaf(deps[..4].to_vec(), signers(40, 3)),
            );
        }
        _ => panic!("mode: sigs [n] | generic"),
    }
    let status = std::fs::read_to_string("/proc/self/status").unwrap_or_default();
    println!(
        "{}",
        status
            .lines()
            .find(|l| l.starts_with("VmHWM"))
            .unwrap_or("VmHWM unknown")
    );
}
