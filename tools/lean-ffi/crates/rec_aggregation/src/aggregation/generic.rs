//! leanSTARK (scheme `0x11`) dependencies: "the leanVM program `vk` commits to accepts `data_hash`".
//!
//! # Verification key
//!
//! `vk = keccak256(verifier::TAG ‖ table)`: upstream's own program digest, the Fiat-Shamir IV every
//! proof of the program is made under (`leanvm_core::cpu::fs_seed`). `table` is the program's stacked
//! bytecode ([`leanvm_core::cpu::bytecode_table`]): all `16 · 2^kgen` words, little-endian, zero padding
//! included, `kgen ≤` [`MAX_GENERIC_BYTECODE_LOG`].
//!
//! The guest seeds the dependency's transcript with the claim's `vk` itself, so the table is bound
//! before the first challenge, and rehashes the hinted table into `vk` with every byte range-checked
//! (the guest's `gen_table` says why the bytes hashed are the words it evaluates). The guest and
//! [`verify_generic`] accept the same claims.
//!
//! # Witness
//!
//! `u8 kgen ‖ table (16 · 2^kgen words, 8 bytes each, little-endian) ‖ proof`, the proof in
//! [`leanvm_core::cpu::Proof::to_bytes`]'s encoding, of an execution of the table on the public input
//! `data_hash` (two 16-byte halves).

use super::cells::count;
use super::cells::pack_16_bytes;
use super::error::AggregationError;
use super::hints::SubHints;
use leanvm_core::cpu::{Proof, from_scalar, scalar_proof};
use primitives::field::F192;

/// The most instructions, as a log, of a leanSTARK dependency's program.
pub const MAX_GENERIC_BYTECODE_LOG: usize = 11;
/// The most leanSTARK claims one aggregation list holds.
pub const MAX_GENERIC_DEPENDENCIES: usize = 16;

/// The domain closing the dependency digest of a list with leanSTARK claims: `"eip8288/leanstark-deps"`, zero-padded.
pub const LEANSTARK_DEPS_DOMAIN: [u8; 32] = *b"eip8288/leanstark-deps\0\0\0\0\0\0\0\0\0\0";

/// Stacked table words a row of the bytecode: the table is `16 · 2^kgen` words.
const ROW_WORDS: usize = 16;

/// A parsed leanSTARK witness.
pub(super) struct GenericWitness {
    pub(super) kgen: usize,
    pub(super) table: Vec<u64>,
    pub(super) proof: Proof,
}

fn table_bytes(table: &[u64]) -> Vec<u8> {
    table.iter().flat_map(|word| word.to_le_bytes()).collect()
}

/// `keccak256(verifier::TAG ‖ table)`, upstream's Fiat-Shamir IV of the table.
fn program_digest(table: &[u64]) -> [u8; 32] {
    primitives::hash::Hasher::new()
        .update(verifier::TAG)
        .update(&table_bytes(table))
        .finalize()
}

/// `kgen` of a table length `16 · 2^kgen`, within the cap.
fn table_log(len: usize) -> Option<usize> {
    let rows = len / ROW_WORDS;
    let kgen = rows.trailing_zeros() as usize;
    (len == rows * ROW_WORDS && rows.is_power_of_two() && kgen <= MAX_GENERIC_BYTECODE_LOG).then_some(kgen)
}

/// The verification key of a program, from its stacked bytecode table ([`leanvm_core::cpu::bytecode_table`]'s
/// words): its program digest. `None` if the table is not `16 · 2^kgen` words with `kgen ≤`
/// [`MAX_GENERIC_BYTECODE_LOG`].
pub fn generic_verification_key(table_words: &[u64]) -> Option<[u8; 32]> {
    table_log(table_words.len())?;
    Some(program_digest(table_words))
}

/// The witness of a leanSTARK dependency on `table_words` with `proof`.
pub fn generic_witness(table_words: &[u64], proof: &Proof) -> Option<Vec<u8>> {
    let kgen = table_log(table_words.len())?;
    let mut out = vec![kgen as u8];
    out.extend(table_bytes(table_words));
    out.extend(proof.to_bytes());
    Some(out)
}

pub(super) fn parse_witness(witness: &[u8]) -> Option<GenericWitness> {
    let (&kgen, rest) = witness.split_first()?;
    let kgen = kgen as usize;
    if kgen > MAX_GENERIC_BYTECODE_LOG {
        return None;
    }
    let (table, proof) = rest.split_at_checked(ROW_WORDS << kgen << 3)?;
    let table = table
        .as_chunks::<8>()
        .0
        .iter()
        .map(|b| u64::from_le_bytes(*b))
        .collect();
    Some(GenericWitness {
        kgen,
        table,
        proof: Proof::from_bytes(proof)?,
    })
}

/// A digest as the two canonical cells the VM takes it in.
pub(super) fn digest_cells(digest: &[u8; 32]) -> [F192; 2] {
    [pack_16_bytes(&digest[..16]), pack_16_bytes(&digest[16..])]
}

/// The verifier's verdict on a parsed witness, with what the guest's witness is built from.
pub(super) fn check(
    data_hash: &[u8; 32],
    vk: &[u8; 32],
    w: &GenericWitness,
) -> Option<(verifier::Bytecode, verifier::Summary)> {
    if program_digest(&w.table) != *vk {
        return None;
    }
    let bytecode = verifier::Bytecode::new(&w.table);
    let summary = verifier::verify_execution(&bytecode, data_hash, &scalar_proof(&w.proof)).ok()?;
    Some((bytecode, summary))
}

/// Native check of a leanSTARK dependency: `witness` parses, its table's program digest is `vk`, and its
/// proof shows an execution of the table on `data_hash`. Accepts exactly what the aggregation guest accepts.
///
/// Also builds the guest's hints, so a proof the native verifier accepts but the guest's fixed capacities
/// (`gen_verify`'s fit check) cannot take is rejected here rather than when the block is proved.
pub fn verify_generic(data_hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    parse_witness(witness).is_some_and(|w| {
        check(data_hash, vk, &w).is_some_and(|(bytecode, summary)| sub_hints(&w, &w, data_hash, &bytecode, &summary).is_ok())
    })
}

/// The guest's hints for one leanSTARK claim (`verify_generic` in the guest): `kgen`, the sub-proof's
/// witness, then the table a row at a time, each byte in the exponent.
pub(super) fn generic_hints(data_hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> Result<SubHints, AggregationError> {
    let w = parse_witness(witness).ok_or(AggregationError::InvalidGenericWitness)?;
    // Test builds only: the sub-proof's hints come from an honest witness, the table from `witness`.
    #[cfg(test)]
    if let Some((honest_hash, honest)) = super::hints::HONEST_GENERIC.with(|h| h.borrow().clone()) {
        let h = parse_witness(&honest).ok_or(AggregationError::InvalidGenericWitness)?;
        let (bytecode, summary) =
            check(&honest_hash, &program_digest(&h.table), &h).ok_or(AggregationError::InvalidGenericWitness)?;
        return sub_hints(&w, &h, &honest_hash, &bytecode, &summary);
    }
    let unchecked = cfg!(feature = "forge") && std::env::var_os("EIP8288_NO_HOST_SIGCHECK").is_some();
    let (bytecode, summary) = match check(data_hash, vk, &w) {
        Some(checked) => checked,
        None if unchecked => {
            let bytecode = verifier::Bytecode::new(&w.table);
            let summary = verifier::replay_unchecked(&bytecode, data_hash, &scalar_proof(&w.proof))
                .map_err(|_| AggregationError::InvalidGenericWitness)?;
            (bytecode, summary)
        }
        None => return Err(AggregationError::InvalidGenericWitness),
    };
    sub_hints(&w, &w, data_hash, &bytecode, &summary)
}

/// The hints of `shown`'s size and table around the sub-proof hints of `proved`'s proof.
fn sub_hints(
    shown: &GenericWitness,
    proved: &GenericWitness,
    data_hash: &[u8; 32],
    bytecode: &verifier::Bytecode,
    summary: &verifier::Summary,
) -> Result<SubHints, AggregationError> {
    let value = from_scalar(
        bytecode
            .evaluate(&summary.bytecode_point)
            .map_err(|_| AggregationError::InvalidGenericWitness)?,
    );
    // The witness depends on the program's sizes only, so any program of its length stands in.
    let stand_in = leanvm_core::cpu::Program::assemble(
        vec![leanvm_core::cpu::Op::Xor { a: 0, b: 0, c: 0 }; 1 << proved.kgen],
        Default::default(),
        0,
    );
    let (sub, _) = super::child::gen_verify(&stand_in, digest_cells(data_hash), &proved.proof, summary, value)?;
    let mut hints: SubHints = vec![("gen_kbc", vec![count(shown.kgen)])];
    hints.extend(sub);
    for row in shown.table.chunks(ROW_WORDS) {
        hints.push((
            "gen_bytes",
            table_bytes(row).into_iter().map(|b| count(b.into())).collect(),
        ));
    }
    Ok(hints)
}
