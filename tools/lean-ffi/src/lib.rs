// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Pinned LeanVM verification and recursive aggregation of complete dependency sets.

use bincode::Options;
use lean_vm::cpu::{self, Program};
use primitives::field::F192;
use rec_aggregation::eip8288_mixed::{self, GenericInput, MixedProof};
use sphincs::{SphincsPublicKey, SphincsSignature};
use std::cell::Cell;
use std::collections::{BTreeSet, HashMap, HashSet};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Barrier, Mutex, OnceLock};
use std::thread::ThreadId;
use tiny_keccak::{Hasher, Keccak};

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_INPUT_BYTES: usize = 18 * 1024 * 1024;
const MAX_DEPS: usize = eip8288_mixed::MAX_DEPENDENCIES;
const MAX_MIXED_GUEST_BYTES: usize = MAX_BYTES - 12 - 96 * MAX_DEPS;
const MAX_INSTRUCTIONS: usize = 1 << eip8288_mixed::MAX_GENERIC_BYTECODE_LOG;
const MAX_OPERAND: u32 = 65535;
const MAX_GENERIC_STARKS: usize = eip8288_mixed::MAX_GENERIC_DEPENDENCIES;
const MAX_DECODE_BYTES: usize = 32 * 1024 * 1024;
const MAGIC: &[u8; 4] = b"NLR3";
static PROVER: Mutex<()> = Mutex::new(());
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

// F192 is GF(2^64)[y]/(y^3+y+1): new stores three distinct u64 coefficients,
// without integer modular reduction. Two (c0,c1,0) cells injectively encode all 256 bits.
fn pi(hash: &[u8; 32]) -> [F192; 2] {
    let word = |i| u64::from_le_bytes(hash[i..i + 8].try_into().unwrap());
    [
        F192::new(word(0), word(8), 0),
        F192::new(word(16), word(24), 0),
    ]
}
fn wire() -> impl Options {
    bincode::DefaultOptions::new()
        .with_fixint_encoding()
        .with_limit(MAX_BYTES as u64)
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
    fn dep(&mut self) -> Result<Dep, ()> {
        let d: Dep = self.take(96)?.try_into().map_err(|_| ())?;
        if d[..31].iter().any(|b| *b != 0) || ![0x10, 0x11].contains(&d[31]) {
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
fn put_number(out: &mut Vec<u8>, n: usize) {
    out.extend_from_slice(&(n as u32).to_le_bytes());
}
fn put_blob(out: &mut Vec<u8>, b: &[u8]) {
    put_number(out, b.len());
    out.extend_from_slice(b);
}
fn put_deps(out: &mut Vec<u8>, ds: &[Dep]) {
    put_number(out, ds.len());
    for d in ds {
        out.extend_from_slice(d);
    }
}
fn commitment(ds: &[Dep]) -> [u8; 32] {
    keccak(&ds.iter().flatten().copied().collect::<Vec<_>>())
}
#[cfg(test)]
fn sphincs_dependency(pk: &SphincsPublicKey, message: &[u8; 32]) -> Dep {
    let mut dep = [0; 96];
    dep[31] = 0x10;
    dep[32..64].copy_from_slice(message);
    dep[64..].copy_from_slice(&keccak(&pk.flatten()));
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
    if keccak(&pk.flatten()) != *vk {
        return Err(());
    }
    let sig = SphincsSignature::from_bytes(witness[32..].try_into().map_err(|_| ())?);
    sphincs::verify(&pk, hash, &sig).map_err(|_| ())?;
    Ok((pk, sig))
}
fn verify_sphincs(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    parse_sphincs(hash, vk, witness).is_ok()
}

// A fixed 45-byte record: opcode, four u32 operands, three u64 immediate limbs,
// then one u32 BLAKE2s metadata operand. Unused fields must be zero.
fn encode_program(program: &Program) -> Vec<u8> {
    use cpu::{DerefMode, Op};
    let mut out = Vec::new();
    put_number(&mut out, program.prog.len());
    for op in &program.prog {
        let (tag, args, limbs, md) = match *op {
            Op::Xor { a, b, c } => (0, [a, b, c, 0], [0; 3], 0),
            Op::Mul { a, b, c } => (1, [a, b, c, 0], [0; 3], 0),
            Op::Set { o, k } => (2, [o, 0, 0, 0], [k.c0, k.c1, k.c2], 0),
            Op::Deref { o1, o2, o3, mode } => (
                3,
                [
                    o1,
                    o2,
                    o3,
                    match mode {
                        DerefMode::Cell => 0,
                        DerefMode::Pc => 1,
                        DerefMode::Fp => 2,
                    },
                ],
                [0; 3],
                0,
            ),
            Op::Jump { oc, od, of } => (4, [oc, od, of, 0], [0; 3], 0),
            Op::Blake2s { ins, cv, out, md } => (5, ins, [cv as u64, out as u64, 0], md),
            Op::Sha3 {
                m,
                cap,
                out,
                digest,
            } => (
                6,
                m[..4].try_into().unwrap(),
                [
                    m[4] as u64 | ((m[5] as u64) << 32),
                    m[6] as u64 | ((m[7] as u64) << 32),
                    cap as u64 | ((out as u64) << 32),
                ],
                digest as u32,
            ),
        };
        out.push(tag);
        for x in args {
            out.extend_from_slice(&x.to_le_bytes());
        }
        for x in limbs {
            out.extend_from_slice(&x.to_le_bytes());
        }
        out.extend_from_slice(&md.to_le_bytes());
    }
    out
}
fn stark_program(code: &[u8]) -> Result<Program, ()> {
    use cpu::{DerefMode, Op};
    let mut r = Reader { bytes: code };
    let n = r.number()?;
    if n == 0 || n > MAX_INSTRUCTIONS || !n.is_power_of_two() || r.bytes.len() != n * 45 {
        return Err(());
    }
    let mut ops = Vec::with_capacity(n);
    for _ in 0..n {
        let tag = r.take(1)?[0];
        let mut a = [0u32; 4];
        let mut k = [0u64; 3];
        for x in &mut a {
            *x = r.number()? as u32;
        }
        for x in &mut k {
            *x = u64::from_le_bytes(r.take(8)?.try_into().map_err(|_| ())?);
        }
        let md = r.number()? as u32;
        // Upstream precomputes g-powers up to the largest operand at assembly.
        if a.iter().any(|v| *v > MAX_OPERAND)
            || md > MAX_OPERAND
            || (tag == 5 && (k[0] > MAX_OPERAND as u64 || k[1] > MAX_OPERAND as u64))
        {
            return Err(());
        }
        let op = match tag {
            0 => Op::Xor {
                a: a[0],
                b: a[1],
                c: a[2],
            },
            1 => Op::Mul {
                a: a[0],
                b: a[1],
                c: a[2],
            },
            2 => Op::Set {
                o: a[0],
                k: F192::new(k[0], k[1], k[2]),
            },
            3 => Op::Deref {
                o1: a[0],
                o2: a[1],
                o3: a[2],
                mode: match a[3] {
                    0 => DerefMode::Cell,
                    1 => DerefMode::Pc,
                    2 => DerefMode::Fp,
                    _ => return Err(()),
                },
            },
            4 => Op::Jump {
                oc: a[0],
                od: a[1],
                of: a[2],
            },
            5 if k[0] <= u32::MAX as u64 && k[1] <= u32::MAX as u64 => Op::Blake2s {
                ins: a,
                cv: k[0] as u32,
                out: k[1] as u32,
                md,
            },
            6 if md <= 1 => {
                let halves = |value: u64| [value as u32, (value >> 32) as u32];
                let m = [
                    a[0],
                    a[1],
                    a[2],
                    a[3],
                    halves(k[0])[0],
                    halves(k[0])[1],
                    halves(k[1])[0],
                    halves(k[1])[1],
                ];
                let [cap, out] = halves(k[2]);
                if m.iter().any(|value| *value > MAX_OPERAND)
                    || cap > MAX_OPERAND - 4
                    || out > MAX_OPERAND - 12
                {
                    return Err(());
                }
                Op::Sha3 {
                    m,
                    cap,
                    out,
                    digest: md != 0,
                }
            }
            _ => return Err(()),
        };
        ops.push(op);
    }
    let program = Program::assemble(ops, HashMap::new(), 0);
    if encode_program(&program) != code {
        return Err(());
    }
    Ok(program)
}
fn decode_stark(
    hash: &[u8; 32],
    vk: &[u8; 32],
    witness: &[u8],
) -> Result<(Program, cpu::Proof), ()> {
    if witness.len() > MAX_BYTES {
        return Err(());
    }
    let mut r = Reader { bytes: witness };
    let code = r.blob()?;
    if keccak(code) != *vk {
        return Err(());
    }
    let program = stark_program(code)?;
    let mut preflight = Preflight::new(r.bytes);
    preflight.cpu()?;
    preflight.end()?;
    let proof: cpu::Proof = wire().deserialize(r.bytes).map_err(|_| ())?;
    if !eip8288_mixed::generic_recursion_supported(&program, &proof) {
        return Err(());
    }
    cpu::verify(&program, &pi(hash), &proof).map_err(|_| ())?;
    Ok((program, proof))
}

fn verify_stark(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    decode_stark(hash, vk, witness).is_ok()
}

struct Aggregate {
    deps: Vec<Dep>,
    proof: Option<MixedProof>,
}

fn decode_aggregate(bytes: &[u8]) -> Result<Aggregate, ()> {
    decode_aggregate_with_hash(bytes, None)
}

fn decode_aggregate_with_hash(bytes: &[u8], expected: Option<&[u8; 32]>) -> Result<Aggregate, ()> {
    if bytes.len() > MAX_BYTES {
        return Err(());
    }
    let mut r = Reader { bytes };
    if r.take(4)? != MAGIC {
        return Err(());
    }
    let deps = r.deps()?;
    if !deps.windows(2).all(|w| w[0] < w[1])
        || deps.iter().filter(|d| d[31] == 0x11).count() > MAX_GENERIC_STARKS
        || expected.is_some_and(|hash| commitment(&deps) != *hash)
    {
        return Err(());
    }
    let payload = r.blob()?;
    r.end()?;
    if payload.len() > MAX_MIXED_GUEST_BYTES || payload.is_empty() != deps.is_empty() {
        return Err(());
    }
    let proof = if deps.is_empty() {
        None
    } else {
        let mut preflight = Preflight::new(payload);
        preflight.mixed()?;
        preflight.end()?;
        let proof = MixedProof::from_bytes_without_deps(&deps, payload).map_err(|_| ())?;
        proof.verify().map_err(|_| ())?;
        Some(proof)
    };
    Ok(Aggregate { deps, proof })
}

const RAW_SIGNATURES_PER_LEAF: usize = 4;
const RAW_GENERICS_PER_LEAF: usize = 1;
const RECURSIVE_FAN_IN: usize = 2;

fn aggregate_bounded(
    children: Vec<MixedProof>,
    raw: Vec<(Dep, SphincsPublicKey, [u8; 32], SphincsSignature)>,
    generic: Vec<(Dep, Program, cpu::Proof)>,
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
    let generic: Vec<_> = generic
        .into_iter()
        .filter(|(dep, _, _)| selected.contains(dep) && !covered.contains(dep) && seen.insert(*dep))
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
    for leaf in generic.chunks(RAW_GENERICS_PER_LEAF) {
        let inputs: Vec<_> = leaf
            .iter()
            .map(|(dep, program, proof)| GenericInput {
                program,
                proof,
                data: dep[32..64].try_into().unwrap(),
            })
            .collect();
        let deps: Vec<_> = leaf
            .iter()
            .map(|(dep, _, _)| *dep)
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        nodes.push(eip8288_mixed::aggregate_mixed(&[], &[], &inputs, &deps, 1).map_err(|_| ())?);
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
            Ok([MAGIC.as_slice(), &[0; 8]].concat())
        } else {
            Err(())
        };
    }
    let _guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    lean_vm::init_prover_pool();
    let mut r = Reader { bytes: input };
    let mut all = Vec::new();
    let mut raw = Vec::new();
    let mut generic = Vec::new();
    for _ in 0..r.count()? {
        let dep = r.dep()?;
        let witness = r.blob()?;
        let data = dep[32..64].try_into().map_err(|_| ())?;
        let key = dep[64..96].try_into().map_err(|_| ())?;
        if dep[31] == 0x10 {
            let (pk, signature) = parse_sphincs(&data, &key, witness)?;
            raw.push((dep, pk, data, signature));
        } else {
            let (program, proof) = decode_stark(&data, &key, witness)?;
            generic.push((dep, program, proof));
        }
        all.push(dep);
    }
    let child_count = r.count()?;
    if child_count > rec_aggregation::MAX_RECURSIONS {
        return Err(());
    }
    let mut children = Vec::new();
    for _ in 0..child_count {
        let declared = r.deps()?;
        let child = decode_aggregate(r.blob()?)?;
        if child.deps
            != declared
                .iter()
                .copied()
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect::<Vec<_>>()
        {
            return Err(());
        }
        all.extend(declared);
        if let Some(proof) = child.proof {
            children.push(proof);
        }
    }
    let discards: HashSet<_> = r.deps()?.into_iter().collect();
    r.end()?;
    let deps: Vec<_> = all
        .into_iter()
        .filter(|dep| !discards.contains(dep))
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    if deps.len() > MAX_DEPS
        || deps.iter().filter(|d| d[31] == 0x11).count() > MAX_GENERIC_STARKS
        || commitment(&deps) != *hash
    {
        return Err(());
    }
    let payload = if deps.is_empty() {
        vec![]
    } else {
        aggregate_bounded(children, raw, generic, &deps)?.to_bytes_without_deps()
    };
    if payload.len() > MAX_MIXED_GUEST_BYTES {
        return Err(());
    }
    let mut out = MAGIC.to_vec();
    put_deps(&mut out, &deps);
    put_blob(&mut out, &payload);
    if out.len() > MAX_BYTES {
        return Err(());
    }
    Ok(out)
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
    .ok()
    .and_then(Result::ok)
    .unwrap_or(false) as i32
}
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    5
}
/// # Safety
/// Output must hold nine writable u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != 9 {
        return 0;
    }
    let limits = [
        MAX_BYTES as u32,
        MAX_DEPS as u32,
        rec_aggregation::MAX_RECURSIONS as u32,
        (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
        MAX_GENERIC_STARKS as u32,
        MAX_INSTRUCTIONS as u32,
        MAX_OPERAND,
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
pub unsafe extern "C" fn nlean_verify_leanstark(
    data: *const u8,
    vk: *const u8,
    witness: *const u8,
    len: usize,
) -> i32 {
    checked(|| {
        Ok(verify_stark(
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
        decode_aggregate_with_hash(unsafe { bytes(proof, len)? }, Some(expected))?;
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

pub fn prove_stark(source: &str, hash: &[u8; 32]) -> Result<(Vec<u8>, [u8; 32]), ()> {
    let _guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    lean_vm::init_prover_pool();
    let program = lean_compiler::compile(&lean_compiler::parse(source).map_err(|_| ())?);
    let key = eip8288_mixed::generic_verification_key(&program).map_err(|_| ())?;
    let code = encode_program(&program);
    let (proof, _) = cpu::prove(&program, pi(hash), 1).map_err(|_| ())?;
    if !eip8288_mixed::generic_recursion_supported(&program, &proof) {
        return Err(());
    }
    let mut out = Vec::new();
    put_blob(&mut out, &code);
    out.extend(wire().serialize(&proof).map_err(|_| ())?);
    if out.len() > MAX_BYTES {
        return Err(());
    }
    Ok((out, key))
}

#[cfg(test)]
mod tests {
    use super::*;
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
        assert_eq!(proof, [MAGIC.as_slice(), &[0; 8]].concat());
        assert!(decode_aggregate_with_hash(&proof, Some(&hash)).is_ok());
        assert!(invalid.is_err());
        let mut wrong_hash = hash;
        wrong_hash[0] ^= 1;
        assert!(decode_aggregate_with_hash(&proof, Some(&wrong_hash)).is_err());
        for malformed in [&[0; 11][..], &[0; 13][..]] {
            assert!(prove_aggregate(&hash, malformed).is_err());
        }
    }

    #[test]
    fn public_input_packing_preserves_every_bit() {
        for bit in 0..256 {
            let mut message = [0; 32];
            message[bit / 8] = 1 << (bit % 8);
            let cells = pi(&message);
            let decoded = [
                cells[0].c0.to_le_bytes(),
                cells[0].c1.to_le_bytes(),
                cells[1].c0.to_le_bytes(),
                cells[1].c1.to_le_bytes(),
            ]
            .concat();
            assert_eq!(decoded, message);
            assert_eq!((cells[0].c2, cells[1].c2), (0, 0));
        }
        let cells = pi(&[255; 32]);
        assert_eq!(
            (cells[0].c0, cells[0].c1, cells[1].c0, cells[1].c1),
            (u64::MAX, u64::MAX, u64::MAX, u64::MAX)
        );
    }
    #[test]
    fn real_signature_and_vm_proof_bind_message_and_key() {
        let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
        let message = [7; 32];
        let signature = [
            pk.flatten().as_slice(),
            sphincs::sign(&sk, &message).to_bytes().as_slice(),
        ]
        .concat();
        assert!(verify_sphincs(&message, &keccak(&pk.flatten()), &signature));
        assert!(!verify_sphincs(
            &[8; 32],
            &keccak(&pk.flatten()),
            &signature
        ));
        assert!(!verify_sphincs(&message, &[0; 32], &signature));
        let mut bad = signature;
        bad[10] ^= 1;
        assert!(!verify_sphincs(&message, &keccak(&pk.flatten()), &bad));
        let mut small = [0; 32];
        small[0] = 7;
        small[16] = 9;
        for input in [small, [255; 32]] {
            let words: Vec<_> = input
                .chunks_exact(8)
                .map(|word| u64::from_le_bytes(word.try_into().unwrap()))
                .collect();
            let source = format!(
                "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = f192({}, {}, 0)\n    p[GEN] = f192({}, {}, 0)\n    return\n",
                words[0], words[1], words[2], words[3]
            );
            let (proof, key) = prove_stark(&source, &input).unwrap();
            assert!(verify_stark(&input, &key, &proof));
            for index in [7, 15, 23, 31] {
                let mut wrong = input;
                wrong[index] ^= 1;
                assert!(!verify_stark(&wrong, &key, &proof));
            }
            assert!(!verify_stark(&input, &[0; 32], &proof));
            let mut bad = proof;
            *bad.last_mut().unwrap() ^= 1;
            assert!(!verify_stark(&input, &key, &bad));
        }
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
        let checked_leaf = decode_aggregate(&leaf).unwrap();
        assert_eq!(checked_leaf.deps, vec![dep]);
        let mut parent = Vec::new();
        put_number(&mut parent, 0);
        put_number(&mut parent, 1);
        put_deps(&mut parent, &[dep]);
        put_blob(&mut parent, &leaf);
        put_deps(&mut parent, &[]);
        let root = prove_aggregate(&commitment(&[dep]), &parent).unwrap();
        assert_eq!(decode_aggregate(&root).unwrap().deps, vec![dep]);
        let mut forged = root.clone();
        forged[4 + 4 + 32] ^= 1;
        assert!(decode_aggregate(&forged).is_err());
        *forged.last_mut().unwrap() ^= 1;
        assert!(decode_aggregate(&forged).is_err());
        let mut discard = Vec::new();
        put_number(&mut discard, 0);
        put_number(&mut discard, 1);
        put_deps(&mut discard, &[dep]);
        put_blob(&mut discard, &leaf);
        put_deps(&mut discard, &[dep]);
        let empty = prove_aggregate(&commitment(&[]), &discard).unwrap();
        assert!(decode_aggregate(&empty).unwrap().deps.is_empty());
        assert!(prove_aggregate(&commitment(&[dep]), &discard).is_err());
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
        assert_eq!(decode_aggregate(&all).unwrap().deps, deps);
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
        assert_eq!(decode_aggregate(&root).unwrap().deps, retained);
        let repeated = combine(&[(&retained, &root)], &[], &retained);
        assert_eq!(decode_aggregate(&repeated).unwrap().deps, retained);
        assert_eq!(root, repeated);
        let redundant = combine(
            &[(&retained, &root), (&deps[..2], &left)],
            &[deps[1]],
            &retained,
        );
        assert_eq!(root, redundant);
        assert!(decode_aggregate_with_hash(&root, Some(&commitment(&deps))).is_err());
    }

    #[test]
    fn malformed_buffers_fail_closed() {
        assert_eq!(
            unsafe {
                nlean_verify_leansphincs(std::ptr::null(), std::ptr::null(), std::ptr::null(), 0)
            },
            0
        );
        assert_eq!(
            unsafe {
                nlean_verify_leanstark([0; 32].as_ptr(), [0; 32].as_ptr(), [0; 8].as_ptr(), 8)
            },
            0
        );
        assert!(decode_aggregate(b"NLR2\xff\xff\xff\xff").is_err());
        for has_generic in [false, true] {
            let mut deps = vec![];
            if has_generic {
                let mut dep = [0; 96];
                dep[31] = 0x11;
                deps.push(dep);
            }
            let mut envelope = MAGIC.to_vec();
            put_deps(&mut envelope, &deps);
            put_blob(&mut envelope, &[0]);
            put_number(&mut envelope, 0);
            assert!(decode_aggregate(&envelope).is_err());
        }
        // Reject malicious nested vector lengths before upstream claim reconstruction.
        let mut declared = [0; 96];
        declared[31] = 0x10;
        for preceding_vectors in 0..8 {
            let mut nested = vec![0; preceding_vectors * 8];
            nested.extend_from_slice(&u64::MAX.to_le_bytes());
            let mut envelope = MAGIC.to_vec();
            put_deps(&mut envelope, &[declared]);
            put_blob(&mut envelope, &nested);
            put_number(&mut envelope, 0);
            assert!(decode_aggregate(&envelope).is_err());
        }
        let core = (
            vec![F192::ZERO; 33],
            [Vec::<F192>::new(), Vec::<F192>::new()],
            cpu::Proof {
                stream: vec![],
                merkle: vec![],
            },
        );
        let mut envelope = MAGIC.to_vec();
        put_deps(&mut envelope, &[declared]);
        put_blob(&mut envelope, &wire().serialize(&core).unwrap());
        assert!(decode_aggregate(&envelope).is_err());
    }
    #[test]
    fn preflight_rejects_allocation_hints_and_excess_generic_claims() {
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
        let mut deps = vec![[0u8; 96]; MAX_GENERIC_STARKS + 1];
        for (i, dep) in deps.iter_mut().enumerate() {
            dep[31] = 0x11;
            dep[63] = i as u8;
        }
        let mut envelope = MAGIC.to_vec();
        put_deps(&mut envelope, &deps);
        assert!(decode_aggregate(&envelope).is_err());
    }
    #[test]
    fn bytecode_offsets_are_bounded_before_upstream_assembly() {
        let mut code = 1u32.to_le_bytes().to_vec();
        code.push(2); // SET
        code.extend_from_slice(&u32::MAX.to_le_bytes());
        code.extend_from_slice(&[0; 40]);
        assert!(stark_program(&code).is_err());
    }
    #[test]
    fn exported_limits_match_and_panics_fail_closed() {
        assert_eq!(nlean_abi_version(), 5);
        let mut key = [0u8; 32];
        assert_eq!(unsafe { nlean_aggregated_vk(key.as_mut_ptr()) }, 1);
        assert_eq!(
            key.iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>(),
            "9370d760abb55fdf02acc7e8d40688c425815c3d25a2aea3c030b2ae1ab51ace"
        );
        let mut limits = [0u32; 9];
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 9) }, 1);
        assert_eq!(
            limits,
            [
                MAX_BYTES as u32,
                MAX_DEPS as u32,
                rec_aggregation::MAX_RECURSIONS as u32,
                (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
                MAX_GENERIC_STARKS as u32,
                MAX_INSTRUCTIONS as u32,
                MAX_OPERAND,
                MAX_INPUT_BYTES as u32,
                MAX_MIXED_GUEST_BYTES as u32
            ]
        );
        assert_eq!(unsafe { nlean_limits(std::ptr::null_mut(), 9) }, 0);
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 8) }, 0);
        assert_eq!(checked(|| panic!("contained upstream failure")), 0);
    }

    #[test]
    fn generic_input_order_does_not_change_canonical_dependencies() {
        let mut entries = Vec::new();
        for value in [7u8, 8] {
            let source = format!(
                "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = {value}\n    p[GEN] = 9\n    return\n"
            );
            let mut message = [0; 32];
            message[0] = value;
            message[16] = 9;
            let (witness, key) = prove_stark(&source, &message).unwrap();
            let mut dep = [0; 96];
            dep[31] = 0x11;
            dep[32..64].copy_from_slice(&message);
            dep[64..].copy_from_slice(&key);
            entries.push((dep, witness));
        }
        entries.sort_by_key(|(dep, _)| *dep);
        let deps: Vec<_> = entries.iter().map(|(dep, _)| *dep).collect();
        for reverse in [false, true] {
            if reverse {
                entries.reverse();
            }
            let mut input = Vec::new();
            put_number(&mut input, entries.len());
            for (dep, witness) in &entries {
                input.extend_from_slice(dep);
                put_blob(&mut input, witness);
            }
            put_number(&mut input, 0);
            put_deps(&mut input, &[]);
            let proof = prove_aggregate(&commitment(&deps), &input).unwrap();
            assert_eq!(decode_aggregate(&proof).unwrap().deps, deps);
            let mut noncanonical = proof;
            for offset in 0..96 {
                noncanonical.swap(8 + offset, 104 + offset);
            }
            assert!(decode_aggregate(&noncanonical).is_err());
        }
    }

    #[test]
    fn recursive_generic_parent_compresses_large_inputs_and_preserves_discards() {
        let mut entries = Vec::new();
        for value in 7u8..23 {
            let source = format!(
                "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = {value}\n    p[GEN] = 9\n    return\n"
            );
            let mut message = [0; 32];
            message[0] = value;
            message[16] = 9;
            let (witness, key) = prove_stark(&source, &message).unwrap();
            let mut dep = [0; 96];
            dep[31] = 0x11;
            dep[32..64].copy_from_slice(&message);
            dep[64..].copy_from_slice(&key);
            entries.push((dep, witness));
        }
        entries.sort_by_key(|(dep, _)| *dep);
        let deps: Vec<_> = entries.iter().map(|(dep, _)| *dep).collect();
        let mut input = Vec::new();
        put_number(&mut input, entries.len());
        for (dep, witness) in &entries {
            input.extend_from_slice(dep);
            put_blob(&mut input, witness);
        }
        put_number(&mut input, 0);
        put_deps(&mut input, &[]);
        assert!(input.len() > 4 * 1024 * 1024 && input.len() <= MAX_INPUT_BYTES);
        let parent = prove_aggregate(&commitment(&deps), &input).unwrap();
        assert!(parent.len() < 1024 * 1024);
        let verified = decode_aggregate(&parent).unwrap();
        assert_eq!(verified.deps, deps);
        assert!(verified.proof.is_some());

        let reaggregate = |retained: &[Dep], discards: &[Dep]| {
            let mut input = Vec::new();
            put_number(&mut input, 0);
            put_number(&mut input, 1);
            put_deps(&mut input, &deps);
            put_blob(&mut input, &parent);
            put_deps(&mut input, discards);
            assert!(input.len() < 1024 * 1024);
            let proof = prove_aggregate(&commitment(retained), &input).unwrap();
            let verified = decode_aggregate(&proof).unwrap();
            assert_eq!(verified.deps, retained);
            assert!(verified.proof.is_some());
            assert!(prove_aggregate(&[0; 32], &input).is_err());
            proof
        };
        let all = reaggregate(&deps, &[]);
        assert_eq!(all, parent);
        let one = reaggregate(&deps[..1], &deps[1..]);
        assert!(one.len() < 1024 * 1024);
        let (sk, pk) = sphincs::key_gen_from_seed([44; 32]);
        let message = [44; 32];
        let sphincs_dep = sphincs_dependency(&pk, &message);
        let signature = [
            pk.flatten().as_slice(),
            sphincs::sign(&sk, &message).to_bytes().as_slice(),
        ]
        .concat();
        let mut mixed_deps = deps.clone();
        mixed_deps.push(sphincs_dep);
        mixed_deps.sort();
        let mut mixed_input = Vec::new();
        put_number(&mut mixed_input, 1);
        mixed_input.extend_from_slice(&sphincs_dep);
        put_blob(&mut mixed_input, &signature);
        put_number(&mut mixed_input, 1);
        put_deps(&mut mixed_input, &deps);
        put_blob(&mut mixed_input, &parent);
        put_deps(&mut mixed_input, &[]);
        let mixed = prove_aggregate(&commitment(&mixed_deps), &mixed_input).unwrap();
        let verified = decode_aggregate(&mixed).unwrap();
        assert_eq!(verified.deps, mixed_deps);
        assert!(verified.proof.unwrap().to_bytes_without_deps().len() <= MAX_MIXED_GUEST_BYTES);
        let mut discard = Vec::new();
        put_number(&mut discard, 0);
        put_number(&mut discard, 1);
        put_deps(&mut discard, &mixed_deps);
        put_blob(&mut discard, &mixed);
        put_deps(&mut discard, &[sphincs_dep]);
        let generic_only = prove_aggregate(&commitment(&deps), &discard).unwrap();
        let verified = decode_aggregate(&generic_only).unwrap();
        assert_eq!(verified.deps, deps);
        assert!(verified.proof.is_some());
        eprintln!(
            "generic16 parent={} retained16={} retained1={} mixed={}",
            parent.len(),
            all.len(),
            one.len(),
            mixed.len()
        );
    }

    #[test]
    fn oversized_root_and_old_carried_envelopes_fail_closed() {
        let mut dep = [0; 96];
        dep[31] = 0x11;
        let mut envelope = MAGIC.to_vec();
        put_deps(&mut envelope, &[dep]);
        put_blob(&mut envelope, &vec![0; MAX_MIXED_GUEST_BYTES + 1]);
        assert!(decode_aggregate(&envelope).is_err());
        assert!(decode_aggregate(b"NLR2\0\0\0\0\0\0\0\0\0\0\0\0").is_err());
        assert!(!verify_stark(&[0; 32], &[0; 32], &vec![0; MAX_BYTES + 1]));
    }

    #[test]
    fn duplicate_generic_claims_compress_once_and_reject_invalid_origins() {
        let source = "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = 7\n    p[GEN] = 9\n    return\n";
        let mut message = [0; 32];
        message[0] = 7;
        message[16] = 9;
        let (first, key) = prove_stark(source, &message).unwrap();
        let program = lean_compiler::compile(&lean_compiler::parse(source).unwrap());
        let mut second = Vec::new();
        put_blob(&mut second, &encode_program(&program));
        let proof = {
            let _guard = PROVER.lock().unwrap();
            cpu::prove(&program, pi(&message), 2).unwrap().0
        };
        cpu::verify(&program, &pi(&message), &proof).unwrap();
        second.extend(wire().serialize(&proof).unwrap());
        assert!(verify_stark(&message, &key, &first));
        assert!(!verify_stark(&message, &key, &second));
        let mut dep = [0; 96];
        dep[31] = 0x11;
        dep[32..64].copy_from_slice(&message);
        dep[64..].copy_from_slice(&key);
        let leaf = |witness: &[u8]| {
            let mut input = Vec::new();
            put_number(&mut input, 1);
            input.extend_from_slice(&dep);
            put_blob(&mut input, witness);
            put_number(&mut input, 0);
            put_deps(&mut input, &[]);
            prove_aggregate(&commitment(&[dep]), &input).unwrap()
        };
        let parent = leaf(&first);
        let mut duplicated = Vec::new();
        put_number(&mut duplicated, 2);
        for _ in 0..2 {
            duplicated.extend_from_slice(&dep);
            put_blob(&mut duplicated, &first);
        }
        put_number(&mut duplicated, 0);
        put_deps(&mut duplicated, &[]);
        let result = prove_aggregate(&commitment(&[dep]), &duplicated).unwrap();
        assert_eq!(decode_aggregate(&result).unwrap().deps, vec![dep]);
        assert!(result.len() < 1024 * 1024);
        let mut input = Vec::new();
        put_number(&mut input, 1);
        input.extend_from_slice(&dep);
        put_blob(&mut input, &first);
        put_number(&mut input, 2);
        for _ in 0..2 {
            put_deps(&mut input, &[dep]);
            put_blob(&mut input, &parent);
        }
        put_deps(&mut input, &[]);
        assert_eq!(
            prove_aggregate(&commitment(&[dep]), &input).unwrap(),
            parent
        );
        let mut bad = first.clone();
        *bad.last_mut().unwrap() ^= 1;
        for rejected in [&bad, &second] {
            let mut input = Vec::new();
            put_number(&mut input, 2);
            for witness in [&first, rejected] {
                input.extend_from_slice(&dep);
                put_blob(&mut input, witness);
            }
            put_number(&mut input, 1);
            put_deps(&mut input, &[dep]);
            put_blob(&mut input, &parent);
            put_deps(&mut input, &[]);
            assert!(prove_aggregate(&commitment(&[dep]), &input).is_err());
        }
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
            0
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
