// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Pinned LeanVM verification and recursive aggregation of complete dependency sets.

use rec_aggregation::eip8288_mixed::{self, MixedProof};
use sphincs::{SphincsPublicKey, SphincsSignature};
use std::cell::Cell;
use std::collections::{BTreeSet, HashSet};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Barrier, Mutex, OnceLock};
use std::thread::ThreadId;
use tiny_keccak::{Hasher, Keccak};

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_INPUT_BYTES: usize = 18 * 1024 * 1024;
const MAX_DEPS: usize = eip8288_mixed::MAX_DEPENDENCIES;
const MAX_MIXED_GUEST_BYTES: usize = MAX_BYTES;
const MAX_DECODE_BYTES: usize = 32 * 1024 * 1024;
static PROVER: Mutex<()> = Mutex::new(());

/// Takes the prover for one proof. The proving arena allows one proof in flight per process; PROVER, behind the managed
/// proving gate, serializes every proof.
fn begin_proof() -> std::sync::MutexGuard<'static, ()> {
    let guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    lean_vm::init_prover();
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
type Dep = [u8; 96];

pub fn keccak(bytes: &[u8]) -> [u8; 32] {
    let mut h = Keccak::v256();
    h.update(bytes);
    let mut out = [0; 32];
    h.finalize(&mut out);
    out
}

pub fn aggregated_vk() -> [u8; 32] {
    eip8288_mixed::mixed_guest_key()
}

// Pinned fixed-int serde layout. Validate every allocation hint without allocating.
struct Preflight<'a> {
    reader: Reader<'a>,
    allocated: usize,
}
impl<'a> Preflight<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self {
            reader: Reader { bytes },
            allocated: 0,
        }
    }
    fn count(&mut self, max: usize, width: usize) -> Result<usize, ()> {
        let n = usize::try_from(u64::from_le_bytes(
            self.reader.take(8)?.try_into().map_err(|_| ())?,
        ))
        .map_err(|_| ())?;
        if n > max || n > self.reader.bytes.len() / width {
            return Err(());
        }
        self.allocated = self
            .allocated
            .checked_add(n.checked_mul(width).ok_or(())?)
            .ok_or(())?;
        if self.allocated > MAX_DECODE_BYTES {
            return Err(());
        }
        Ok(n)
    }
    fn vector(&mut self, max: usize, width: usize) -> Result<(), ()> {
        let n = self.count(max, width)?;
        self.reader.take(n * width)?;
        Ok(())
    }
    fn cpu(&mut self) -> Result<(), ()> {
        self.vector(MAX_BYTES / 24, 24)?; // scalar stream
        let phases = self.count(128, 16)?;
        for _ in 0..phases {
            let rows = self.count(4096, 8)?;
            self.allocated = self.allocated.checked_add(rows * 24).ok_or(())?;
            if self.allocated > MAX_DECODE_BYTES {
                return Err(());
            }
            for _ in 0..rows {
                self.vector(MAX_BYTES / 8, 8)?;
            }
            self.vector(MAX_BYTES / 32, 32)?;
        }
        Ok(())
    }
    fn mixed(&mut self) -> Result<(), ()> {
        self.vector(32, 24)?; // bytecode evaluation point
        self.vector(32, 24)?; // BLAKE2s matrix evaluation point
        self.vector(32, 24)?; // Keccak matrix evaluation point
        self.cpu()
    }
    fn end(self) -> Result<(), ()> {
        self.reader.end()
    }
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
    // EIP-8288 enables leanSPHINCS (0x10) only; leanSTARK (0x11) is reserved.
    fn dep(&mut self) -> Result<Dep, ()> {
        let d: Dep = self.take(96)?.try_into().map_err(|_| ())?;
        if d[..31].iter().any(|b| *b != 0) || d[31] != 0x10 {
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
#[cfg(test)]
fn put_number(out: &mut Vec<u8>, n: usize) {
    out.extend_from_slice(&(n as u32).to_le_bytes());
}
#[cfg(test)]
fn put_blob(out: &mut Vec<u8>, b: &[u8]) {
    put_number(out, b.len());
    out.extend_from_slice(b);
}
#[cfg(test)]
fn put_deps(out: &mut Vec<u8>, ds: &[Dep]) {
    put_number(out, ds.len());
    for d in ds {
        out.extend_from_slice(d);
    }
}
/// EIP-8288 `get_deps_hash` of a canonical list, as the recursive guest commits to it.
pub fn commitment(ds: &[Dep]) -> [u8; 32] {
    eip8288_mixed::deps_hash(ds)
}
fn canonical(ds: Vec<Dep>) -> Vec<Dep> {
    ds.into_iter()
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}
#[cfg(test)]
fn sphincs_dependency(pk: &SphincsPublicKey, message: &[u8; 32]) -> Dep {
    let mut dep = [0; 96];
    dep[31] = 0x10;
    dep[32..64].copy_from_slice(message);
    dep[64..].copy_from_slice(&eip8288_mixed::sphincs_verification_key(pk));
    dep
}
fn parse_sphincs(
    hash: &[u8; 32],
    vk: &[u8; 32],
    witness: &[u8],
) -> Result<(SphincsPublicKey, SphincsSignature), ()> {
    if witness.len() != sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE {
        return Err(());
    }
    let pk = SphincsPublicKey::from_bytes(witness[..32].try_into().map_err(|_| ())?);
    if eip8288_mixed::sphincs_verification_key(&pk) != *vk {
        return Err(());
    }
    let sig = SphincsSignature::from_bytes(witness[32..].try_into().map_err(|_| ())?);
    sphincs::verify(&pk, hash, &sig).map_err(|_| ())?;
    Ok((pk, sig))
}
fn verify_sphincs(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    parse_sphincs(hash, vk, witness).is_ok()
}

// EIP-8288 `stark_proof`: the guest proof alone, checked against `deps_hash` and the pinned key.
fn preflight_proof(proof: &[u8]) -> Result<(), ()> {
    if proof.is_empty() || proof.len() > MAX_MIXED_GUEST_BYTES {
        return Err(());
    }
    let mut preflight = Preflight::new(proof);
    preflight.mixed()?;
    preflight.end()
}

fn verify_aggregate(hash: &[u8; 32], proof: &[u8]) -> Result<(), ()> {
    preflight_proof(proof)?;
    eip8288_mixed::verify_mixed_deps(hash, proof).map_err(|_| ())
}

// A recursive child is proven against its declared dependencies, which the caller supplies;
// the upstream decoder enforces their canonical order and profile bounds.
fn decode_child(deps: &[Dep], proof: &[u8]) -> Result<MixedProof, ()> {
    preflight_proof(proof)?;
    let proof = MixedProof::from_bytes_without_deps(deps, proof).map_err(|_| ())?;
    proof.verify().map_err(|_| ())?;
    Ok(proof)
}

const RAW_SIGNATURES_PER_LEAF: usize = 4;
const RECURSIVE_FAN_IN: usize = 2;

fn aggregate_bounded(
    children: Vec<MixedProof>,
    raw: Vec<(Dep, SphincsPublicKey, [u8; 32], SphincsSignature)>,
    desired: &[Dep],
) -> Result<MixedProof, ()> {
    let selected: BTreeSet<_> = desired.iter().copied().collect();
    let mut nodes: Vec<_> = children
        .into_iter()
        .filter(|child| {
            child
                .dependencies()
                .iter()
                .any(|claim| selected.contains(claim))
        })
        .collect();
    if let Some(index) = nodes.iter().position(|node| node.dependencies() == desired) {
        return Ok(nodes.swap_remove(index));
    }
    let covered: HashSet<_> = nodes
        .iter()
        .flat_map(|node| node.dependencies().iter().copied())
        .collect();
    let mut seen = HashSet::new();
    let raw: Vec<_> = raw
        .into_iter()
        .filter(|(dep, _, _, _)| {
            selected.contains(dep) && !covered.contains(dep) && seen.insert(*dep)
        })
        .collect();
    for leaf in raw.chunks(RAW_SIGNATURES_PER_LEAF) {
        let signatures: Vec<_> = leaf
            .iter()
            .map(|(_, pk, message, signature)| (*pk, *message, signature.clone()))
            .collect();
        let deps: Vec<_> = leaf
            .iter()
            .map(|(dep, _, _, _)| *dep)
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        nodes
            .push(eip8288_mixed::aggregate_mixed(&[], &signatures, &[], &deps, 1).map_err(|_| ())?);
    }
    while nodes.len() > 1 {
        let mut next = Vec::new();
        let mut input = nodes.into_iter();
        while let Some(first) = input.next() {
            let mut batch = vec![first];
            batch.extend(input.by_ref().take(RECURSIVE_FAN_IN - 1));
            let deps: Vec<_> = batch
                .iter()
                .flat_map(|node| node.dependencies().iter().copied())
                .filter(|dep| selected.contains(dep))
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect();
            if batch.len() == 1 && batch[0].dependencies() == deps {
                next.push(batch.pop().ok_or(())?);
            } else {
                next.push(
                    eip8288_mixed::aggregate_mixed(&batch, &[], &[], &deps, 1).map_err(|_| ())?,
                );
            }
        }
        nodes = next;
    }
    let node = nodes.pop().ok_or(())?;
    if node.dependencies() == desired {
        return Ok(node);
    }
    eip8288_mixed::aggregate_mixed(&[node], &[], &[], desired, 1).map_err(|_| ())
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
        let witness = r.blob()?;
        let data = dep[32..64].try_into().map_err(|_| ())?;
        let key = dep[64..96].try_into().map_err(|_| ())?;
        let (pk, signature) = parse_sphincs(&data, &key, witness)?;
        raw.push((dep, pk, data, signature));
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
        children.push(decode_child(&declared, proof)?);
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
    if deps.len() > MAX_DEPS || commitment(&deps) != *hash {
        return Err(());
    }
    if deps.is_empty() {
        return Ok(vec![]);
    }
    let proof = aggregate_bounded(children, raw, &deps)?.to_bytes_without_deps();
    if proof.len() > MAX_MIXED_GUEST_BYTES {
        return Err(());
    }
    Ok(proof)
}

// Crypto ABI calls bound buffers and contain upstream panics; free requires its exact owned allocation.
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
/// so callers never read a fault as a verdict. Every `checked` export shares this contract, including
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
    .map_or(-1, |verdict| verdict.unwrap_or(false) as i32)
}
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    8
}
/// # Safety
/// Output must hold six writable u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != 6 {
        return 0;
    }
    let limits = [
        MAX_BYTES as u32,
        MAX_DEPS as u32,
        rec_aggregation::MAX_RECURSIONS as u32,
        (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
        MAX_INPUT_BYTES as u32,
        MAX_MIXED_GUEST_BYTES as u32,
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
    use bincode::Options;
    use lean_vm::cpu;
    use primitives::field::F192;

    fn wire() -> impl Options {
        bincode::DefaultOptions::new()
            .with_fixint_encoding()
            .with_limit(MAX_BYTES as u64)
    }
    #[test]
    fn exact_empty_proof_bypasses_an_occupied_prover() {
        let hash = commitment(&[]);
        let guard = PROVER
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let (send, receive) = std::sync::mpsc::channel();
        let worker = std::thread::spawn(move || {
            let valid = prove_aggregate(&hash, &[0; 12]);
            let mut wrong_hash = hash;
            wrong_hash[0] ^= 1;
            let invalid = prove_aggregate(&wrong_hash, &[0; 12]);
            send.send((valid, invalid)).unwrap();
        });
        let result = receive.recv_timeout(std::time::Duration::from_secs(3));
        // Release before joining even if the fast path regresses to taking the lock.
        drop(guard);
        worker.join().unwrap();
        let (proof, invalid) = result.expect("empty proof must not wait for the prover");
        let proof = proof.unwrap();
        // EIP-8288: no dependencies, empty stark_proof; callers check that case without the guest.
        assert!(proof.is_empty());
        assert!(verify_aggregate(&hash, &proof).is_err());
        assert!(invalid.is_err());
        for malformed in [&[0; 11][..], &[0; 13][..]] {
            assert!(prove_aggregate(&hash, malformed).is_err());
        }
    }

    /// Expected digests computed in Python from the EIP-8288 `get_deps_hash` pseudocode.
    #[test]
    fn dependency_commitment_matches_eip_pseudocode_vectors() {
        let dep = |data: [u8; 32], key: [u8; 32]| {
            let mut dep = [0; 96];
            dep[31] = 0x10;
            dep[32..64].copy_from_slice(&data);
            dep[64..].copy_from_slice(&key);
            dep
        };
        let key = |start: u8| std::array::from_fn(|i| start + i as u8);
        let (a, b, c, d) = (
            dep([7; 32], key(0)),
            dep([7; 32], key(1)),
            dep([1; 32], [0xff; 32]),
            dep([0xa5; 32], [0x5a; 32]),
        );
        for (deps, expected) in [
            (
                vec![],
                "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9",
            ),
            (
                vec![a],
                "6921fc8275ae947e76ab603255f550b615341fb880a8635996d30b55201d28f6",
            ),
            (
                vec![b, a, c, a],
                "27ad454def73a88020d3f7a6428cd790cf354b531b0447013027e7fa277b9e13",
            ),
            (
                vec![d, a, c, b],
                "830b7c9edb9a816e25929dbae987d3fcf2e900fa009d930c60eddacb0d479ed8",
            ),
        ] {
            let digest = commitment(&canonical(deps));
            assert_eq!(
                digest
                    .iter()
                    .map(|b| format!("{b:02x}"))
                    .collect::<String>(),
                expected
            );
        }
    }

    #[test]
    fn real_signature_binds_message_and_key() {
        let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
        let message = [7; 32];
        let signature = [
            pk.flatten().as_slice(),
            sphincs::sign(&sk, &message).to_bytes().as_slice(),
        ]
        .concat();
        assert!(verify_sphincs(&message, &pk.flatten(), &signature));
        assert!(!verify_sphincs(&[8; 32], &pk.flatten(), &signature));
        assert!(!verify_sphincs(&message, &[0; 32], &signature));
        assert!(!verify_sphincs(
            &message,
            &keccak(&pk.flatten()),
            &signature
        ));
        let mut bad = signature;
        bad[10] ^= 1;
        assert!(!verify_sphincs(&message, &pk.flatten(), &bad));
    }
    #[test]
    fn real_recursive_composition_and_discards_bind_full_dependency_set() {
        let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
        let message = [7; 32];
        let signature = [
            pk.flatten().as_slice(),
            sphincs::sign(&sk, &message).to_bytes().as_slice(),
        ]
        .concat();
        let dep = sphincs_dependency(&pk, &message);
        let mut input = Vec::new();
        put_number(&mut input, 1);
        input.extend_from_slice(&dep);
        put_blob(&mut input, &signature);
        put_number(&mut input, 0);
        put_deps(&mut input, &[]);
        let leaf = prove_aggregate(&commitment(&[dep]), &input).unwrap();
        verify_aggregate(&commitment(&[dep]), &leaf).unwrap();
        assert!(
            !leaf.windows(96).any(|bytes| bytes == dep),
            "stark_proof carries no dependency"
        );
        let mut parent = Vec::new();
        put_number(&mut parent, 0);
        put_number(&mut parent, 1);
        put_deps(&mut parent, &[dep]);
        put_blob(&mut parent, &leaf);
        put_deps(&mut parent, &[]);
        let root = prove_aggregate(&commitment(&[dep]), &parent).unwrap();
        verify_aggregate(&commitment(&[dep]), &root).unwrap();
        let mut other = dep;
        other[40] ^= 1;
        assert!(verify_aggregate(&commitment(&[other]), &root).is_err());
        let mut forged = root.clone();
        *forged.last_mut().unwrap() ^= 1;
        assert!(verify_aggregate(&commitment(&[dep]), &forged).is_err());
        let mut discard = Vec::new();
        put_number(&mut discard, 0);
        put_number(&mut discard, 1);
        put_deps(&mut discard, &[dep]);
        put_blob(&mut discard, &leaf);
        put_deps(&mut discard, &[dep]);
        let empty = prove_aggregate(&commitment(&[]), &discard).unwrap();
        assert!(empty.is_empty());
        assert!(prove_aggregate(&commitment(&[dep]), &discard).is_err());
        // get_deps_hash omits the scheme, so callers and the guest admit leanSPHINCS only: leanSTARK
        // (reserved) and unknown schemes fail in direct, recursive and discard lists alike.
        let mut restated = dep;
        restated[31] = 0x11;
        assert_eq!(commitment(&[restated]), commitment(&[dep]));
        for scheme in [0x00, 0x11, 0x12] {
            let mut unknown = dep;
            unknown[31] = scheme;
            let mut direct = Vec::new();
            put_number(&mut direct, 1);
            direct.extend_from_slice(&unknown);
            put_blob(&mut direct, &signature);
            put_number(&mut direct, 0);
            put_deps(&mut direct, &[]);
            let mut nested = Vec::new();
            put_number(&mut nested, 0);
            put_number(&mut nested, 1);
            put_deps(&mut nested, &[dep, unknown]);
            put_blob(&mut nested, &leaf);
            put_deps(&mut nested, &[unknown]);
            let mut discarded = Vec::new();
            put_number(&mut discarded, 0);
            put_number(&mut discarded, 1);
            put_deps(&mut discarded, &[dep]);
            put_blob(&mut discarded, &leaf);
            put_deps(&mut discarded, &[unknown]);
            for input in [direct, nested, discarded] {
                assert!(prove_aggregate(&commitment(&[dep]), &input).is_err());
            }
        }
    }
    #[test]
    fn binary_recursion_preserves_overlapping_parents_and_partial_discards() {
        let (sk, pk) = sphincs::key_gen_from_seed([43; 32]);
        let mut deps = Vec::new();
        let mut input = Vec::new();
        put_number(&mut input, 3);
        for index in 0..3u8 {
            let message = [index; 32];
            let dep = sphincs_dependency(&pk, &message);
            let witness = [
                pk.flatten().as_slice(),
                sphincs::sign(&sk, &message).to_bytes().as_slice(),
            ]
            .concat();
            deps.push(dep);
            input.extend_from_slice(&dep);
            put_blob(&mut input, &witness);
        }
        deps.sort();
        put_number(&mut input, 0);
        put_deps(&mut input, &[]);
        let all = prove_aggregate(&commitment(&deps), &input).unwrap();
        verify_aggregate(&commitment(&deps), &all).unwrap();
        let combine = |parents: &[(&[Dep], &[u8])], discards: &[Dep], expected: &[Dep]| {
            let mut input = Vec::new();
            put_number(&mut input, 0);
            put_number(&mut input, parents.len());
            for (inner, proof) in parents {
                put_deps(&mut input, inner);
                put_blob(&mut input, proof);
            }
            put_deps(&mut input, discards);
            prove_aggregate(&commitment(expected), &input).unwrap()
        };
        let left = combine(&[(&deps, &all)], &[deps[2]], &deps[..2]);
        let right = combine(&[(&deps, &all)], &[deps[0]], &deps[1..]);
        let retained = vec![deps[0], deps[2]];
        let root = combine(
            &[(&deps[..2], &left), (&deps[1..], &right)],
            &[deps[1]],
            &retained,
        );
        verify_aggregate(&commitment(&retained), &root).unwrap();
        let repeated = combine(&[(&retained, &root)], &[], &retained);
        verify_aggregate(&commitment(&retained), &repeated).unwrap();
        assert_eq!(root, repeated);
        let redundant = combine(
            &[(&retained, &root), (&deps[..2], &left)],
            &[deps[1]],
            &retained,
        );
        assert_eq!(root, redundant);
        assert!(verify_aggregate(&commitment(&deps), &root).is_err());
    }

    #[test]
    fn malformed_buffers_fail_closed() {
        assert_eq!(
            unsafe {
                nlean_verify_leansphincs(std::ptr::null(), std::ptr::null(), std::ptr::null(), 0)
            },
            0
        );
        let mut declared = [0; 96];
        declared[31] = 0x10;
        let hash = commitment(&[declared]);
        assert!(verify_aggregate(&hash, &[0xff; 8]).is_err());
        assert!(verify_aggregate(&hash, &[0]).is_err());
        // Reject malicious nested vector lengths before upstream claim reconstruction.
        for preceding_vectors in 0..8 {
            let mut nested = vec![0; preceding_vectors * 8];
            nested.extend_from_slice(&u64::MAX.to_le_bytes());
            assert!(verify_aggregate(&hash, &nested).is_err());
            assert!(decode_child(&[declared], &nested).is_err());
        }
        let core = (
            vec![F192::ZERO; 33],
            [Vec::<F192>::new(), Vec::<F192>::new()],
            cpu::Proof {
                stream: vec![],
                merkle: vec![],
            },
        );
        assert!(verify_aggregate(&hash, &wire().serialize(&core).unwrap()).is_err());
    }
    #[test]
    fn preflight_rejects_allocation_hints_and_leanstark_claims() {
        let mut stream = u64::MAX.to_le_bytes().to_vec();
        stream.extend_from_slice(&0u64.to_le_bytes());
        assert!(Preflight::new(&stream).cpu().is_err());
        for (phases, rows, words) in [(129, 0, 0), (1, 4097, 0), (1, 1, u64::MAX)] {
            let mut proof = 0u64.to_le_bytes().to_vec();
            proof.extend_from_slice(&(phases as u64).to_le_bytes());
            proof.extend_from_slice(&(rows as u64).to_le_bytes());
            proof.extend_from_slice(&words.to_le_bytes());
            assert!(Preflight::new(&proof).cpu().is_err());
        }
        let mut deps = vec![[0u8; 96]];
        deps[0][31] = 0x11;
        let mut input = Vec::new();
        put_number(&mut input, 0);
        put_number(&mut input, 1);
        put_deps(&mut input, &deps);
        put_blob(&mut input, &[0]);
        put_deps(&mut input, &[]);
        assert!(decode_child(&deps, &[0]).is_err());
        assert!(prove_aggregate(&commitment(&deps), &input).is_err());
    }
    #[test]
    fn exported_limits_match_and_panics_fail_closed() {
        assert_eq!(nlean_abi_version(), 8);
        let mut key = [0u8; 32];
        assert_eq!(unsafe { nlean_aggregated_vk(key.as_mut_ptr()) }, 1);
        assert_eq!(
            key.iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>(),
            "6b99f3872b5dc2347ca13a348f1f297192fab75a92d362697544745f4a981166"
        );
        let mut limits = [0u32; 6];
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 6) }, 1);
        assert_eq!(
            limits,
            [
                MAX_BYTES as u32,
                MAX_DEPS as u32,
                rec_aggregation::MAX_RECURSIONS as u32,
                (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
                MAX_INPUT_BYTES as u32,
                MAX_MIXED_GUEST_BYTES as u32
            ]
        );
        assert_eq!(unsafe { nlean_limits(std::ptr::null_mut(), 6) }, 0);
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 9) }, 0);
        assert_eq!(checked(|| panic!("contained upstream failure")), -1);
        assert_eq!(checked(|| Err(())), 0);
    }

    #[test]
    fn oversized_root_fails_closed() {
        let mut dep = [0; 96];
        dep[31] = 0x10;
        let hash = commitment(&[dep]);
        assert!(verify_aggregate(&hash, &vec![0; MAX_MIXED_GUEST_BYTES + 1]).is_err());
    }

    #[test]
    fn quiet_hook_preserves_unrelated_threads() {
        const CHILD: &str = "NLEAN_QUIET_HOOK_TEST";
        if std::env::var_os(CHILD).is_none() {
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "tests::quiet_hook_preserves_unrelated_threads",
                    "--test-threads=1",
                ])
                .env(CHILD, "1")
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            return;
        }
        let diagnostics = std::sync::Arc::new(AtomicUsize::new(0));
        let observer = diagnostics.clone();
        std::panic::set_hook(Box::new(move |_| {
            observer.fetch_add(1, Ordering::Relaxed);
        }));
        assert_eq!(
            checked(|| {
                assert!(
                    std::thread::spawn(|| panic!("unrelated caller"))
                        .join()
                        .is_err()
                );
                let count = parallel::num_threads();
                let barrier = Barrier::new(count);
                let dispatcher = std::thread::current().id();
                parallel::for_each(count, |_| {
                    barrier.wait();
                    if std::thread::current().id() != dispatcher {
                        panic!("contained upstream worker");
                    }
                });
                panic!("contained calling thread");
            }),
            -1
        );
        assert_eq!(diagnostics.load(Ordering::Relaxed), 1);
        assert!(std::panic::catch_unwind(|| panic!("caller outside ABI")).is_err());
        assert_eq!(diagnostics.load(Ordering::Relaxed), 2);
    }

    #[test]
    fn aggregation_input_allows_maximum_child_framing() {
        let mut input = Vec::new();
        put_number(&mut input, 0);
        put_number(&mut input, 2);
        for _ in 0..2 {
            put_deps(&mut input, &[]);
            put_blob(&mut input, &vec![0; MAX_BYTES]);
        }
        put_deps(&mut input, &[]);
        assert!(input.len() > MAX_BYTES && input.len() <= MAX_INPUT_BYTES);
        // Buffer admission differs from proof validity; the fabricated child is still rejected.
        assert!(unsafe { bytes_with_limit(input.as_ptr(), input.len(), MAX_INPUT_BYTES) }.is_ok());
        assert!(unsafe { bytes(input.as_ptr(), input.len()) }.is_err());
        assert!(prove_aggregate(&commitment(&[]), &input).is_err());
        assert!(
            unsafe { bytes_with_limit(std::ptr::null(), MAX_INPUT_BYTES + 1, MAX_INPUT_BYTES) }
                .is_err()
        );
    }
}
