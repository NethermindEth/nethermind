// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Pinned LeanVM verification and recursive aggregation of complete dependency sets.

use rec_aggregation::{
    EthereumClaims, EthereumProof, MAX_GENERIC_BYTECODE_LOG, MAX_GENERIC_DEPENDENCIES,
};
use signatures::lean_sphincs::{self, LeanSphincsPublicKey, LeanSphincsSignature};
use std::cell::{Cell, RefCell};
use std::collections::{BTreeSet, HashSet};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Barrier, Mutex, OnceLock};
use std::thread::ThreadId;

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_INPUT_BYTES: usize = 18 * 1024 * 1024;
/// A local bound: one guest node proves this many signatures in about 9 GiB.
const MAX_DEPS: usize = 256;
// The upstream prover's timings assume its allocator: jemalloc keeps a proof's freed pages mapped for the next one.
// It serves this library's allocations only, and holds their peak until the process exits.
#[global_allocator]
static ALLOCATOR: bench::Jemalloc = bench::Jemalloc;
const LOG_INV_RATE: usize = 1;
static PROVER: Mutex<()> = Mutex::new(());

/// Takes the prover for one proof: `rec_aggregation::aggregate` allows one call at a time per process.
fn begin_proof() -> std::sync::MutexGuard<'static, ()> {
    let guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    parallel::init();
    guard
}

// leanVM's carry-less-multiply field arithmetic is about 68x slower without these features. An
// environment RUSTFLAGS replaces .cargo/config.toml's target-cpu=native instead of adding to it, so
// refuse to build a backend that silently lost them.
#[cfg(any(
    all(target_arch = "x86_64", not(target_feature = "pclmulqdq")),
    all(target_arch = "aarch64", not(target_feature = "aes"))
))]
compile_error!(
    "nethermind_lean needs carry-less multiply: build from tools/lean-ffi so .cargo/config.toml \
     applies, or include -C target-cpu=native (or +pclmulqdq / +aes) in RUSTFLAGS"
);
static ACTIVE_ABI_CALLS: AtomicUsize = AtomicUsize::new(0);
static ABI_WORKERS: OnceLock<Vec<ThreadId>> = OnceLock::new();
thread_local! { static IN_ABI: Cell<bool> = const { Cell::new(false) }; }
thread_local! { static LAST_PANIC: RefCell<String> = const { RefCell::new(String::new()) }; }
type Dep = [u8; 96];
const LEAN_SPHINCS: u8 = 0x10;
/// EIP-8288 reserves this scheme; accepting it is an experimental extension of this stack.
const LEAN_STARK: u8 = 0x11;

pub fn keccak(bytes: &[u8]) -> [u8; 32] {
    primitives::hash::hash(bytes)
}

pub fn aggregated_vk() -> [u8; 32] {
    rec_aggregation::aggregated_vk()
}

struct Reader<'a> {
    bytes: &'a [u8],
}
impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], ()> {
        if n > self.bytes.len() {
            return Err(());
        }
        let (a, b) = self.bytes.split_at(n);
        self.bytes = b;
        Ok(a)
    }
    fn number(&mut self) -> Result<usize, ()> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().map_err(|_| ())?) as usize)
    }
    fn count(&mut self) -> Result<usize, ()> {
        let n = self.number()?;
        if n > MAX_DEPS { Err(()) } else { Ok(n) }
    }
    fn blob(&mut self) -> Result<&'a [u8], ()> {
        let n = self.number()?;
        if n > MAX_BYTES {
            return Err(());
        }
        self.take(n)
    }
    fn dep(&mut self) -> Result<Dep, ()> {
        let d: Dep = self.take(96)?.try_into().map_err(|_| ())?;
        if d[..31].iter().any(|b| *b != 0) || !matches!(d[31], LEAN_SPHINCS | LEAN_STARK) {
            return Err(());
        }
        Ok(d)
    }
    fn deps(&mut self) -> Result<Vec<Dep>, ()> {
        (0..self.count()?).map(|_| self.dep()).collect()
    }
    fn end(self) -> Result<(), ()> {
        if self.bytes.is_empty() {
            Ok(())
        } else {
            Err(())
        }
    }
}

/// A dependency triple's `(data_hash, public key)`; triples and claims sort alike.
/// EIP-8288 leanSPHINCS `verification_key_hash`: the key itself, root then public parameter.
pub fn verification_key(pk: &LeanSphincsPublicKey) -> [u8; 32] {
    let mut out = [0; 32];
    out[..16].copy_from_slice(&pk.root);
    out[16..].copy_from_slice(&pk.public_param);
    out
}
fn public_key(vk: &[u8; 32]) -> LeanSphincsPublicKey {
    LeanSphincsPublicKey {
        root: vk[..16].try_into().unwrap(),
        public_param: vk[16..].try_into().unwrap(),
    }
}
fn data(dep: &Dep) -> [u8; 32] {
    dep[32..64].try_into().unwrap()
}
fn key(dep: &Dep) -> [u8; 32] {
    dep[64..].try_into().unwrap()
}
fn claims(deps: &[Dep]) -> EthereumClaims {
    let of = |scheme| deps.iter().filter(move |dep| dep[31] == scheme);
    EthereumClaims {
        lean_sphincs: of(LEAN_SPHINCS)
            .map(|dep| (data(dep), public_key(&key(dep))))
            .collect(),
        generic: of(LEAN_STARK).map(|dep| (data(dep), key(dep))).collect(),
        ..Default::default()
    }
}
/// The dependency digest of a canonical list, as the recursive guest commits to it: EIP-8288 `get_deps_hash` for a
/// leanSPHINCS-only list, and the split-domain digest of `rec_aggregation::deps_hash` once it holds a leanSTARK entry.
pub fn commitment(ds: &[Dep]) -> [u8; 32] {
    let claims = claims(ds);
    rec_aggregation::deps_hash(&claims.lean_sphincs, &claims.generic)
}
fn generic_count(ds: &[Dep]) -> usize {
    ds.iter().filter(|dep| dep[31] == LEAN_STARK).count()
}
fn canonical(ds: Vec<Dep>) -> Vec<Dep> {
    ds.into_iter()
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}
fn parse_sphincs(
    vk: &[u8; 32],
    witness: &[u8],
) -> Result<(LeanSphincsPublicKey, LeanSphincsSignature), ()> {
    if witness.len() != lean_sphincs::PUB_KEY_SIZE + lean_sphincs::SIG_SIZE || witness[..32] != *vk
    {
        return Err(());
    }
    let sig = LeanSphincsSignature::from_bytes(witness[32..].try_into().map_err(|_| ())?);
    Ok((public_key(vk), sig))
}
fn verify_sphincs(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    parse_sphincs(vk, witness).is_ok_and(|(pk, sig)| lean_sphincs::verify(&pk, hash, &sig).is_ok())
}
fn verify_witness(dep: &Dep, witness: &[u8]) -> bool {
    if dep[31] == LEAN_STARK {
        rec_aggregation::verify_generic(&data(dep), &key(dep), witness)
    } else {
        verify_sphincs(&data(dep), &key(dep), witness)
    }
}

/// A leanVM program that accepts every public input: the leanSTARK dependency anyone can prove. For tests and fixtures.
pub const ANY_INPUT_PROGRAM: &str = "def main():\n    h = StackBuf(2)\n    hint_witness(h, \"pi\")\n    p = 1\n    p[1] = h[0]\n    p[GEN] = h[1]\n    return\n";

/// Compiles `source`, a leanVM program reading its public input from the `pi` hint, and proves it on `hash`: the
/// leanSTARK witness and verification key of the dependency `(hash, key)`. For tests and fixtures.
pub fn prove_stark(source: &str, hash: &[u8; 32]) -> Result<(Vec<u8>, [u8; 32]), ()> {
    let _guard = begin_proof();
    let mut program = lean_compiler::compile(&lean_compiler::parse(source).map_err(|_| ())?);
    let table: Vec<u64> = leanvm_core::cpu::bytecode_table(program.prog())
        .iter()
        .map(|word| word.0)
        .collect();
    let key = rec_aggregation::generic_verification_key(&table).ok_or(())?;
    let input = fiat_shamir::merkle::hash_to_scalars(hash);
    program.set_witness("pi", vec![input.to_vec()]);
    let (proof, _) = leanvm_core::cpu::prove(&program, input, LOG_INV_RATE).map_err(|_| ())?;
    Ok((
        rec_aggregation::generic_witness(&table, &proof).ok_or(())?,
        key,
    ))
}

// EIP-8288 `stark_proof`: the guest proof alone, checked against `deps_hash` and the pinned key.
// The empty proof of an empty list never reaches the guest: callers decide that case themselves.
fn verify_aggregate(hash: &[u8; 32], proof: &[u8]) -> Result<(), ()> {
    if proof.is_empty() || proof.len() > MAX_BYTES {
        return Err(());
    }
    rec_aggregation::verify_deps_hash(hash, proof).map_err(|_| ())
}

// A recursive child is proven against its declared dependencies, which the caller supplies.
fn decode_child(deps: &[Dep], proof: &[u8]) -> Result<EthereumProof, ()> {
    if proof.is_empty() || proof.len() > MAX_BYTES {
        return Err(());
    }
    let proof = EthereumProof::from_proof_bytes(proof, claims(deps)).map_err(|_| ())?;
    proof.verify().map_err(|_| ())?;
    Ok(proof)
}

pub fn prove_aggregate(hash: &[u8; 32], input: &[u8]) -> Result<Vec<u8>, ()> {
    if input.len() > MAX_INPUT_BYTES {
        return Err(());
    }
    if input == [0; 12] {
        return if *hash == commitment(&[]) {
            Ok(vec![])
        } else {
            Err(())
        };
    }
    let _guard = begin_proof();
    let mut r = Reader { bytes: input };
    let mut all = Vec::new();
    let mut raw = Vec::new();
    for _ in 0..r.count()? {
        let dep = r.dep()?;
        raw.push((dep, r.blob()?));
        all.push(dep);
    }
    let child_count = r.count()?;
    if child_count > rec_aggregation::MAX_RECURSIONS {
        return Err(());
    }
    let mut children = Vec::new();
    for _ in 0..child_count {
        let declared = canonical(r.deps()?);
        let proof = r.blob()?;
        if declared.is_empty() {
            if !proof.is_empty() {
                return Err(());
            }
            continue;
        }
        if generic_count(&declared) > MAX_GENERIC_DEPENDENCIES {
            return Err(());
        }
        children.push((decode_child(&declared, proof)?, proof, declared.clone()));
        all.extend(declared);
    }
    let discards: HashSet<_> = r.deps()?.into_iter().collect();
    r.end()?;
    let union: HashSet<_> = all.iter().copied().collect();
    if !discards.is_subset(&union) {
        return Err(());
    }
    let deps: Vec<_> = all
        .into_iter()
        .filter(|dep| !discards.contains(dep))
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    if deps.len() > MAX_DEPS
        || generic_count(&deps) > MAX_GENERIC_DEPENDENCIES
        || commitment(&deps) != *hash
    {
        return Err(());
    }
    if deps.is_empty() {
        return Ok(vec![]);
    }
    let kept: HashSet<_> = deps.iter().copied().collect();
    children.retain(|(_, _, declared)| declared.iter().any(|dep| kept.contains(dep)));
    let covered: HashSet<_> = children
        .iter()
        .flat_map(|(_, _, declared)| declared.iter().copied())
        .collect();
    // The guest checks only the witnesses it is given; one that a child covers or a discard drops is checked here,
    // so an invalid witness fails the call whether or not the proof needs it.
    let (raw, unused): (Vec<_>, Vec<_>) = raw
        .into_iter()
        .partition(|(dep, _)| kept.contains(dep) && !covered.contains(dep));
    if unused
        .iter()
        .any(|(dep, witness)| !verify_witness(dep, witness))
    {
        return Err(());
    }
    if let Some((_, bytes, _)) = children.iter().find(|(_, _, declared)| *declared == deps) {
        return Ok(bytes.to_vec());
    }
    let mut signatures = Vec::new();
    let mut generic = Vec::new();
    for (dep, witness) in raw {
        if dep[31] == LEAN_STARK {
            generic.push((data(&dep), key(&dep), witness.to_vec()));
        } else {
            let (pk, signature) = parse_sphincs(&key(&dep), witness)?;
            signatures.push((pk, data(&dep), signature));
        }
    }
    let children: Vec<_> = children.into_iter().map(|(child, ..)| child).collect();
    let proof = rec_aggregation::aggregate(
        &children,
        generic,
        signatures,
        &[],
        Some(&claims(&deps)),
        LOG_INV_RATE,
    )
    .map_err(|_| ())?
    .proof_bytes();
    if proof.len() > MAX_BYTES {
        return Err(());
    }
    Ok(proof)
}

unsafe fn bytes<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], ()> {
    unsafe { bytes_with_limit(ptr, len, MAX_BYTES) }
}
unsafe fn bytes_with_limit<'a>(ptr: *const u8, len: usize, limit: usize) -> Result<&'a [u8], ()> {
    if len > limit || (ptr.is_null() && len != 0) {
        return Err(());
    }
    if len == 0 {
        Ok(&[])
    } else {
        Ok(unsafe { std::slice::from_raw_parts(ptr, len) })
    }
}
/// Runs one ABI call: returns 1 for accepted, 0 for rejected input and -1 for a contained panic,
/// so callers never read a fault as a verdict. The panic message stays readable through `nlean_last_error`. Every `checked` export shares this contract, including
/// `nlean_aggregated_vk` and `nlean_prove_recursive`, where the C# side accepts only `== 1` as success.
fn checked(f: impl FnOnce() -> Result<bool, ()>) -> i32 {
    static HOOK: OnceLock<()> = OnceLock::new();
    struct CallGuard(bool);
    impl Drop for CallGuard {
        fn drop(&mut self) {
            ACTIVE_ABI_CALLS.fetch_sub(1, Ordering::AcqRel);
            IN_ABI.with(|flag| flag.set(self.0));
        }
    }
    let _guard = CallGuard(IN_ABI.with(|flag| flag.replace(true)));
    ACTIVE_ABI_CALLS.fetch_add(1, Ordering::AcqRel);
    LAST_PANIC.with(|message| message.borrow_mut().clear());
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        HOOK.get_or_init(|| {
            let previous = std::panic::take_hook();
            std::panic::set_hook(Box::new(move |info| {
                let abi_worker = ACTIVE_ABI_CALLS.load(Ordering::Acquire) != 0
                    && ABI_WORKERS
                        .get()
                        .is_some_and(|workers| workers.contains(&std::thread::current().id()));
                if !IN_ABI.with(Cell::get) && !abi_worker {
                    previous(info);
                }
            }));
        });
        ABI_WORKERS.get_or_init(|| {
            let dispatcher = std::thread::current().id();
            parallel::init();
            let count = parallel::num_threads();
            let barrier = Barrier::new(count);
            let workers = Mutex::new(Vec::new());
            // One blocked task per worker identifies this pinned pool without thread-name assumptions.
            parallel::for_each(count, |_| {
                let id = std::thread::current().id();
                if id != dispatcher {
                    workers.lock().unwrap().push(id);
                }
                barrier.wait();
            });
            workers.into_inner().unwrap()
        });
        f()
    }))
    .map_or_else(
        |payload| {
            let message = payload
                .downcast_ref::<&str>()
                .map(|text| text.to_string())
                .or_else(|| payload.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "non-string panic payload".to_string());
            LAST_PANIC.with(|last| *last.borrow_mut() = message);
            -1
        },
        |verdict| verdict.unwrap_or(false) as i32,
    )
}
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    12
}
/// Copies up to `len` bytes of the calling thread's last contained panic message into `out` and returns its full
/// length; empty unless that thread's latest `checked` call returned -1.
///
/// # Safety
/// `out` must reference `len` writable bytes, or be null with `len` 0.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_last_error(out: *mut u8, len: usize) -> usize {
    LAST_PANIC.with(|message| {
        let message = message.borrow();
        let copied = message.len().min(len);
        if !out.is_null() {
            // SAFETY: copied <= len, and the caller guarantees `out` holds len writable bytes.
            unsafe { std::ptr::copy_nonoverlapping(message.as_ptr(), out, copied) };
        }
        message.len()
    })
}
/// # Safety
/// Output must hold seven writable u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != 7 {
        return 0;
    }
    let limits = [
        MAX_BYTES as u32,
        MAX_DEPS as u32,
        rec_aggregation::MAX_RECURSIONS as u32,
        (lean_sphincs::PUB_KEY_SIZE + lean_sphincs::SIG_SIZE) as u32,
        MAX_INPUT_BYTES as u32,
        MAX_GENERIC_DEPENDENCIES as u32,
        1 << MAX_GENERIC_BYTECODE_LOG,
    ];
    unsafe {
        std::ptr::copy_nonoverlapping(limits.as_ptr(), out, limits.len());
    }
    1
}
/// # Safety
/// Input pointers must reference their declared lengths; output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_aggregated_vk(out: *mut u8) -> i32 {
    if out.is_null() {
        return 0;
    }
    checked(|| {
        let key = aggregated_vk();
        unsafe {
            std::ptr::copy_nonoverlapping(key.as_ptr(), out, 32);
        }
        Ok(true)
    })
}
/// # Safety
/// Input pointers must reference their declared lengths; output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_verify_leansphincs(
    data: *const u8,
    vk: *const u8,
    witness: *const u8,
    len: usize,
) -> i32 {
    checked(|| {
        Ok(verify_sphincs(
            unsafe { bytes(data, 32)? }.try_into().map_err(|_| ())?,
            unsafe { bytes(vk, 32)? }.try_into().map_err(|_| ())?,
            unsafe { bytes(witness, len)? },
        ))
    })
}
/// # Safety
/// Input pointers must reference their declared lengths; output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_verify_leanstark(
    data: *const u8,
    vk: *const u8,
    witness: *const u8,
    len: usize,
) -> i32 {
    checked(|| {
        Ok(rec_aggregation::verify_generic(
            unsafe { bytes(data, 32)? }.try_into().map_err(|_| ())?,
            unsafe { bytes(vk, 32)? }.try_into().map_err(|_| ())?,
            unsafe { bytes(witness, len)? },
        ))
    })
}
/// # Safety
/// Input pointers must reference their declared lengths; output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_verify_recursive(
    hash: *const u8,
    vk: *const u8,
    vk_len: usize,
    proof: *const u8,
    len: usize,
) -> i32 {
    checked(|| {
        if unsafe { bytes(vk, vk_len)? } != aggregated_vk() {
            return Ok(false);
        }
        let expected = unsafe { bytes(hash, 32)? }.try_into().map_err(|_| ())?;
        verify_aggregate(expected, unsafe { bytes(proof, len)? })?;
        Ok(true)
    })
}
/// # Safety
/// Input pointers must reference their declared lengths; output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_prove_recursive(
    hash: *const u8,
    vk: *const u8,
    vk_len: usize,
    input: *const u8,
    input_len: usize,
    out: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    checked(|| {
        if out.is_null() || out_len.is_null() || unsafe { bytes(vk, vk_len)? } != aggregated_vk() {
            return Ok(false);
        }
        let proof = prove_aggregate(
            unsafe { bytes(hash, 32)? }.try_into().map_err(|_| ())?,
            unsafe { bytes_with_limit(input, input_len, MAX_INPUT_BYTES)? },
        )?;
        let boxed = proof.into_boxed_slice();
        let len = boxed.len();
        let ptr = Box::into_raw(boxed) as *mut u8;
        unsafe {
            *out = ptr;
            *out_len = len;
        }
        Ok(true)
    })
}
/// # Safety
/// The pointer and length must be the exact allocation returned by nlean_prove_recursive, freed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        unsafe {
            drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn signed(index: u8) -> (Dep, Vec<u8>) {
        let (sk, pk) = lean_sphincs::key_gen_from_seed([index; 32]);
        let message = keccak(&[index]);
        let mut dep = [0; 96];
        dep[31] = 0x10;
        dep[32..64].copy_from_slice(&message);
        dep[64..].copy_from_slice(&verification_key(&pk));
        let signature = lean_sphincs::sign(&sk, &message).unwrap();
        (
            dep,
            [&verification_key(&pk)[..], &signature.to_bytes()[..]].concat(),
        )
    }
    fn put_number(out: &mut Vec<u8>, n: usize) {
        out.extend_from_slice(&(n as u32).to_le_bytes());
    }
    fn input(raw: &[(Dep, Vec<u8>)], children: &[(&[Dep], &[u8])], discards: &[Dep]) -> Vec<u8> {
        let mut out = Vec::new();
        put_number(&mut out, raw.len());
        for (dep, witness) in raw {
            out.extend_from_slice(dep);
            put_number(&mut out, witness.len());
            out.extend_from_slice(witness);
        }
        put_number(&mut out, children.len());
        for (deps, proof) in children {
            put_number(&mut out, deps.len());
            out.extend(deps.iter().flatten());
            put_number(&mut out, proof.len());
            out.extend_from_slice(proof);
        }
        put_number(&mut out, discards.len());
        out.extend(discards.iter().flatten());
        out
    }
    fn hash_of(deps: &[Dep]) -> [u8; 32] {
        commitment(&canonical(deps.to_vec()))
    }

    #[test]
    fn commitment_is_keccak_of_the_64_byte_entries() {
        let deps = canonical((0..3).map(|i| signed(i).0).collect());
        let entries: Vec<u8> = deps.iter().flat_map(|d| d[32..].iter().copied()).collect();
        assert_eq!(commitment(&deps), keccak(&entries));
        assert_eq!(commitment(&[]), keccak(&[]));
    }

    #[test]
    fn empty_list_has_the_empty_proof_and_never_reaches_the_guest() {
        let hash = commitment(&[]);
        assert!(prove_aggregate(&hash, &[0; 12]).unwrap().is_empty());
        assert!(prove_aggregate(&[1; 32], &[0; 12]).is_err());
        assert!(verify_aggregate(&hash, &[]).is_err());
    }

    fn proven(index: u8) -> (Dep, Vec<u8>) {
        let message = keccak(&[0x51, index]);
        let (witness, key) = prove_stark(ANY_INPUT_PROGRAM, &message).unwrap();
        let mut dep = [0; 96];
        dep[31] = LEAN_STARK;
        dep[32..64].copy_from_slice(&message);
        dep[64..].copy_from_slice(&key);
        (dep, witness)
    }

    #[test]
    fn rejects_bad_witnesses_and_unknown_schemes() {
        let (dep, witness) = signed(1);
        assert!(verify_witness(&dep, &witness));
        let mut forged = witness.clone();
        forged[100] ^= 1;
        assert!(!verify_witness(&dep, &forged));
        assert!(prove_aggregate(&hash_of(&[dep]), &input(&[(dep, forged)], &[], &[])).is_err());
        // A signature is no leanSTARK witness, and the scheme is part of the dependency.
        let mut stark = dep;
        stark[31] = LEAN_STARK;
        assert!(!verify_witness(&stark, &witness));
        assert_ne!(hash_of(&[stark]), hash_of(&[dep]));
        assert!(
            prove_aggregate(
                &hash_of(&[stark]),
                &input(&[(stark, witness.clone())], &[], &[])
            )
            .is_err()
        );
        let mut unknown = dep;
        unknown[31] = 0x12;
        assert!(
            prove_aggregate(&hash_of(&[dep]), &input(&[(unknown, witness)], &[], &[])).is_err()
        );
    }

    #[test]
    fn mixed_commitment_joins_both_lists_under_a_domain() {
        let sphincs = canonical((0..2).map(|i| signed(i).0).collect());
        let mut stark = sphincs[0];
        stark[31] = LEAN_STARK;
        let list = |deps: &[Dep]| {
            keccak(
                &deps
                    .iter()
                    .flat_map(|d| d[32..].iter().copied())
                    .collect::<Vec<u8>>(),
            )
        };
        let mut domain = [0u8; 32];
        domain[..22].copy_from_slice(b"eip8288/leanstark-deps");
        let joined =
            |s: &[Dep], g: &[Dep]| keccak(&[&list(s)[..], &list(g)[..], &domain[..]].concat());
        assert_eq!(
            commitment(&canonical([&sphincs[..], &[stark]].concat())),
            joined(&sphincs, &[stark])
        );
        assert_eq!(commitment(&[stark]), joined(&[], &[stark]));
        assert_ne!(commitment(&[stark]), commitment(&sphincs[..1]));
    }

    #[test]
    fn proves_and_merges_leanstark_with_leansphincs() {
        let (stark, stark_witness) = proven(1);
        let (other, other_witness) = proven(2);
        let (sig, sig_witness) = signed(3);
        assert!(verify_witness(&stark, &stark_witness));
        assert!(!verify_witness(&other, &stark_witness));
        let mut forged = stark_witness.clone();
        *forged.last_mut().unwrap() ^= 1;
        assert!(!verify_witness(&stark, &forged));
        assert!(prove_aggregate(&hash_of(&[stark]), &input(&[(stark, forged)], &[], &[])).is_err());

        let mixed = [stark, sig];
        let leaf = prove_aggregate(
            &hash_of(&mixed),
            &input(&[(stark, stark_witness), (sig, sig_witness)], &[], &[]),
        )
        .unwrap();
        assert!(verify_aggregate(&hash_of(&mixed), &leaf).is_ok());
        assert!(verify_aggregate(&hash_of(&[sig]), &leaf).is_err());
        assert!(verify_aggregate(&hash_of(&[stark]), &leaf).is_err());

        // A mixed child plus a raw leanSTARK witness, with the child's signature discarded.
        let declared = canonical(mixed.to_vec());
        let children = [(&declared[..], &leaf[..])];
        let kept = [stark, other];
        let merged = prove_aggregate(
            &hash_of(&kept),
            &input(&[(other, other_witness)], &children, &[sig]),
        )
        .unwrap();
        assert!(verify_aggregate(&hash_of(&kept), &merged).is_ok());
        assert!(verify_aggregate(&hash_of(&[stark, other, sig]), &merged).is_err());
    }

    #[test]
    fn proves_merges_and_discards() {
        let raw: Vec<_> = (0..5).map(signed).collect();
        let deps: Vec<Dep> = raw.iter().map(|(dep, _)| *dep).collect();

        let left = canonical(deps[..2].to_vec());
        let left_proof = prove_aggregate(&hash_of(&left), &input(&raw[..2], &[], &[])).unwrap();
        assert!(verify_aggregate(&hash_of(&left), &left_proof).is_ok());
        assert!(verify_aggregate(&hash_of(&deps[..3]), &left_proof).is_err());
        let mut tampered = left_proof.clone();
        *tampered.last_mut().unwrap() ^= 1;
        assert!(verify_aggregate(&hash_of(&left), &tampered).is_err());

        let right = canonical(deps[2..4].to_vec());
        let right_proof = prove_aggregate(&hash_of(&right), &input(&raw[2..4], &[], &[])).unwrap();

        // Two children and one raw signature, with one child dependency discarded.
        let children = [(&left[..], &left_proof[..]), (&right[..], &right_proof[..])];
        let kept = [deps[1], deps[2], deps[3], deps[4]];
        let merged =
            prove_aggregate(&hash_of(&kept), &input(&raw[4..], &children, &deps[..1])).unwrap();
        assert!(verify_aggregate(&hash_of(&kept), &merged).is_ok());
        assert!(verify_aggregate(&hash_of(&deps), &merged).is_err());

        // A child declared with dependencies it does not prove is refused.
        let lying = [(&right[..], &left_proof[..])];
        assert!(prove_aggregate(&hash_of(&right), &input(&[], &lying, &[])).is_err());
        // A child that already covers the request is returned as is.
        assert_eq!(
            prove_aggregate(&hash_of(&left), &input(&[], &children[..1], &[])).unwrap(),
            left_proof
        );
    }
}
