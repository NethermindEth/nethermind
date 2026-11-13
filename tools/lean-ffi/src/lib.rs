// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Pinned LeanVM verification and recursive aggregation of complete dependency sets.

use bincode::Options;
use lean_vm::cpu::{self, Program};
use primitives::field::F192;
use rec_aggregation::eip8288_mixed::{self, GenericInput, MixedProof};
use sphincs::{SphincsPublicKey, SphincsSignature};
use std::cell::{Cell, RefCell};
use std::collections::{BTreeSet, HashMap, HashSet};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Barrier, Condvar, Mutex, OnceLock};
use std::thread::ThreadId;
use tiny_keccak::{Hasher, Keccak};

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_INPUT_BYTES: usize = 18 * 1024 * 1024;
const MAX_DEPS: usize = eip8288_mixed::MAX_DEPENDENCIES;
const MAX_MIXED_GUEST_BYTES: usize = MAX_BYTES;
const MAX_INSTRUCTIONS: usize = 1 << eip8288_mixed::MAX_GENERIC_BYTECODE_LOG;
const MAX_OPERAND: u32 = 65535;
const MAX_CLAIMS_PER_BATCH_LEAF: usize = eip8288_mixed::MAX_CLAIMS_PER_BATCH_LEAF;
/// EIP-8288 `MAX_INNER_PROOFS_PER_STEP`: batch leaves, direct leanSTARK proofs as singleton leaves, and recursive proofs
/// in one aggregation call. The guest enforces it per execution too; raw leanSPHINCS witnesses do not count.
pub const MAX_INNER_PROOFS_PER_STEP: usize = eip8288_mixed::MAX_INNER_PROOFS_PER_STEP;
const MAX_DECODE_BYTES: usize = 32 * 1024 * 1024;
static PROVER: Mutex<()> = Mutex::new(());

/// Takes the prover for one proof. The proving arena allows one proof in flight per process; PROVER, behind the managed
/// proving gate, serializes every proof.
fn begin_proof() -> std::sync::MutexGuard<'static, ()> {
    let guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    static ARENA: std::sync::Once = std::sync::Once::new();
    ARENA.call_once(|| {
        lean_vm::init_prover();
        restore_system_heap();
    });
    guard
}

/// Undoes the process-wide glibc tuning `init_prover` applies, restoring the operator's setting or glibc's default.
///
/// The arena is its own mapping and does not rely on that tuning, while the host would otherwise never trim its heap or
/// mmap large allocations again. glibc's adaptive mmap threshold stays off: `init_prover` already disabled it, and no
/// mallopt re-enables it.
#[cfg(all(target_os = "linux", target_env = "gnu"))]
fn restore_system_heap() {
    let trim = glibc_setting("trim_threshold", "MALLOC_TRIM_THRESHOLD_", 128 * 1024);
    let mmap_max = glibc_setting("mmap_max", "MALLOC_MMAP_MAX_", 65536);
    // SAFETY: mallopt only adjusts allocator tuning parameters.
    unsafe {
        libc::mallopt(libc::M_TRIM_THRESHOLD, trim);
        libc::mallopt(libc::M_MMAP_MAX, mmap_max);
    }
}

/// The `glibc.malloc.<tunable>` value from `GLIBC_TUNABLES`, else the legacy environment alias, else `default`.
#[cfg(all(target_os = "linux", target_env = "gnu"))]
fn glibc_setting(tunable: &str, alias: &str, default: i32) -> i32 {
    let key = format!("glibc.malloc.{tunable}=");
    std::env::var("GLIBC_TUNABLES")
        .ok()
        .and_then(|tunables| {
            tunables
                .split(':')
                .find_map(|entry| parse_c_integer(entry.strip_prefix(key.as_str())?))
        })
        .or_else(|| parse_c_integer(&std::env::var(alias).ok()?))
        .unwrap_or(default)
}

/// Parses an unsigned integer as `strtoul(s, _, 0)` does (decimal, `0x` hex or leading-`0` octal), saturating at `i32::MAX`.
#[cfg(all(target_os = "linux", target_env = "gnu"))]
fn parse_c_integer(value: &str) -> Option<i32> {
    let parsed = if let Some(hex) = value
        .strip_prefix("0x")
        .or_else(|| value.strip_prefix("0X"))
    {
        u64::from_str_radix(hex, 16)
    } else if value.len() > 1 && value.starts_with('0') {
        u64::from_str_radix(&value[1..], 8)
    } else {
        value.parse()
    };
    parsed.ok().map(|v| i32::try_from(v).unwrap_or(i32::MAX))
}
#[cfg(not(all(target_os = "linux", target_env = "gnu")))]
fn restore_system_heap() {}

/// Keeps verifications off arena phase openings. A thread's `ArenaVec`s die when it next allocates after a phase opens,
/// and an allocation racing the opening can land in the stale phase, so no phase may open while a verification runs.
/// Verifications may run inside an open phase, on their own slabs, so they wait only for an opening, not for the proof
/// node: a node takes several seconds on a few cores, which held up block import and wrapper admission.
struct PhaseGate {
    state: Mutex<GateState>,
    changed: Condvar,
}

struct GateState {
    verifications: usize,
    /// The arena generation an opening started from, while one is pending or its node is still proving.
    opening_from: Option<usize>,
}

static PHASE_GATE: PhaseGate = PhaseGate {
    state: Mutex::new(GateState {
        verifications: 0,
        opening_from: None,
    }),
    changed: Condvar::new(),
};

/// How often a verification held at an opening checks whether the phase has opened, which nothing signals.
const OPENING_POLL: std::time::Duration = std::time::Duration::from_millis(1);

impl PhaseGate {
    fn lock(&self) -> std::sync::MutexGuard<'_, GateState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

struct OpeningGuard;

impl Drop for OpeningGuard {
    fn drop(&mut self) {
        PHASE_GATE.lock().opening_from = None;
        PHASE_GATE.changed.notify_all();
    }
}

/// Runs `prove`, which must open at most one arena phase, once no verification is in flight.
fn in_arena_phase<T>(prove: impl FnOnce() -> T) -> T {
    let mut state = PHASE_GATE.lock();
    let generation = zk_alloc::stats().phases;
    state.opening_from = Some(generation);
    while state.verifications > 0 {
        state = PHASE_GATE
            .changed
            .wait(state)
            .unwrap_or_else(std::sync::PoisonError::into_inner);
    }
    drop(state);
    let _opening = OpeningGuard;
    let result = prove();
    assert!(
        zk_alloc::stats().phases - generation <= 1,
        "a gated prove opened more than one arena phase"
    );
    result
}

/// Runs `verify` outside any arena phase opening: before it, or once the phase it waits for has opened.
fn verify_between_phases<T>(verify: impl FnOnce() -> T) -> T {
    let mut state = PHASE_GATE.lock();
    while let Some(generation) = state.opening_from {
        if zk_alloc::stats().phases != generation {
            break;
        }
        state = PHASE_GATE
            .changed
            .wait_timeout(state, OPENING_POLL)
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .0;
    }
    state.verifications += 1;
    drop(state);
    let _verification = VerificationGuard;
    verify()
}

struct VerificationGuard;

impl Drop for VerificationGuard {
    fn drop(&mut self) {
        let mut state = PHASE_GATE.lock();
        state.verifications -= 1;
        if state.verifications == 0 {
            PHASE_GATE.changed.notify_all();
        }
    }
}

fn aggregate_mixed(
    children: &[MixedProof],
    signatures: &[(SphincsPublicKey, [u8; 32], SphincsSignature)],
    generic: &[GenericInput],
    deps: &[Dep],
) -> Result<MixedProof, ()> {
    in_arena_phase(|| eip8288_mixed::aggregate_mixed(children, signatures, generic, deps, 1))
        .map_err(|_| ())
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

/// EIP-8288 `get_batch_hash`: a batch leaf's public statement over its claims.
pub fn batch_hash(claims: &[[u8; 32]]) -> [u8; 32] {
    eip8288_mixed::batch_hash(claims)
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
/// The instruction count of `code` if its length matches it, checked without decoding any instruction.
fn instruction_count(code: &[u8]) -> Result<usize, ()> {
    let mut r = Reader { bytes: code };
    let n = r.number()?;
    if n == 0 || n > MAX_INSTRUCTIONS || !n.is_power_of_two() || r.bytes.len() != n * 45 {
        return Err(());
    }
    Ok(n)
}
fn stark_program(code: &[u8]) -> Result<Program, ()> {
    use cpu::{DerefMode, Op};
    let n = instruction_count(code)?;
    let mut r = Reader { bytes: &code[4..] };
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
/// Decodes a leanSTARK witness and verifies it for public statement `statement` under `vk`.
fn decode_stark(
    statement: &[u8; 32],
    vk: &[u8; 32],
    witness: &[u8],
) -> Result<(Program, cpu::Proof), ()> {
    if witness.len() > MAX_BYTES {
        return Err(());
    }
    let mut r = Reader { bytes: witness };
    let program = stark_program(r.blob()?)?;
    if eip8288_mixed::generic_verification_key(&program).map_err(|_| ())? != *vk {
        return Err(());
    }
    let mut preflight = Preflight::new(r.bytes);
    preflight.cpu()?;
    preflight.end()?;
    let proof: cpu::Proof = wire().deserialize(r.bytes).map_err(|_| ())?;
    if !eip8288_mixed::generic_recursion_supported(&program, &proof) {
        return Err(());
    }
    cpu::verify(&program, &pi(statement), &proof).map_err(|_| ())?;
    Ok((program, proof))
}

/// Verifies a batch leaf witness of `claims` under `vk`: a nonempty, bounded, strictly increasing claim list and a
/// leanSTARK proof of `get_batch_hash(claims)`.
pub fn verify_batch_leaf(claims: &[[u8; 32]], vk: &[u8; 32], witness: &[u8]) -> bool {
    eip8288_mixed::valid_batch_claims(claims)
        && verify_between_phases(|| decode_stark(&batch_hash(claims), vk, witness).is_ok())
}

/// Verifies a direct leanSTARK proof of `hash`: EIP-8288's singleton leaf statement `get_batch_hash([hash])`.
fn verify_stark(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    verify_batch_leaf(&[*hash], vk, witness)
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

/// Verifies an aggregate `stark_proof` against the dependency commitment `hash` under the pinned guest key.
pub fn verify_aggregate(hash: &[u8; 32], proof: &[u8]) -> Result<(), ()> {
    preflight_proof(proof)?;
    verify_between_phases(|| eip8288_mixed::verify_mixed_deps(hash, proof).map_err(|_| ()))
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

/// A verified batch leaf: its claims, verification key hash, program and proof.
type BatchLeaf = (Vec<[u8; 32]>, [u8; 32], Program, cpu::Proof);

fn leanstark_dependency(claim: &[u8; 32], key: &[u8; 32]) -> Dep {
    let mut dep = [0; 96];
    dep[31] = 0x11;
    dep[32..64].copy_from_slice(claim);
    dep[64..].copy_from_slice(key);
    dep
}

fn aggregate_bounded(
    children: Vec<MixedProof>,
    raw: Vec<(Dep, SphincsPublicKey, [u8; 32], SphincsSignature)>,
    leaves: Vec<BatchLeaf>,
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
    // A leaf proves when one of its selected claims is not yet covered; it publishes all its selected claims.
    let leaves: Vec<_> = leaves
        .into_iter()
        .filter(|(claims, key, _, _)| {
            claims.iter().fold(false, |fresh, claim| {
                let dep = leanstark_dependency(claim, key);
                (selected.contains(&dep) && !covered.contains(&dep) && seen.insert(dep)) || fresh
            })
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
        nodes.push(aggregate_mixed(&[], &signatures, &[], &deps)?);
    }
    for (claims, key, program, proof) in &leaves {
        let deps: Vec<_> = claims
            .iter()
            .map(|claim| leanstark_dependency(claim, key))
            .filter(|dep| selected.contains(dep))
            .collect();
        let input = GenericInput {
            program,
            proof,
            claims,
        };
        nodes.push(aggregate_mixed(&[], &[], &[input], &deps)?);
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
                next.push(aggregate_mixed(&batch, &[], &[], &deps)?);
            }
        }
        nodes = next;
    }
    let node = nodes.pop().ok_or(())?;
    if node.dependencies() == desired {
        return Ok(node);
    }
    aggregate_mixed(&[node], &[], &[], desired)
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
    let mut leaves = Vec::new();
    let mut inner_proofs = 0;
    for _ in 0..r.count()? {
        let dep = r.dep()?;
        let witness = r.blob()?;
        let data = dep[32..64].try_into().map_err(|_| ())?;
        let key = dep[64..96].try_into().map_err(|_| ())?;
        if dep[31] == 0x10 {
            let (pk, signature) = parse_sphincs(&data, &key, witness)?;
            raw.push((dep, pk, data, signature));
        } else {
            inner_proofs += 1;
            if inner_proofs > MAX_INNER_PROOFS_PER_STEP {
                return Err(());
            }
            let (program, proof) = decode_stark(&batch_hash(&[data]), &key, witness)?;
            leaves.push((vec![data], key, program, proof));
        }
        all.push(dep);
    }
    let child_count = r.count()?;
    inner_proofs += child_count;
    if inner_proofs > MAX_INNER_PROOFS_PER_STEP {
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
    // An optional trailing section carries batch leaves; it is absent when there are none.
    if !r.bytes.is_empty() {
        let leaf_count = r.number()?;
        inner_proofs += leaf_count;
        if leaf_count == 0 || inner_proofs > MAX_INNER_PROOFS_PER_STEP {
            return Err(());
        }
        for _ in 0..leaf_count {
            let key: [u8; 32] = r.take(32)?.try_into().map_err(|_| ())?;
            let claim_count = r.number()?;
            if !(1..=MAX_CLAIMS_PER_BATCH_LEAF).contains(&claim_count) {
                return Err(());
            }
            let claims = (0..claim_count)
                .map(|_| r.take(32)?.try_into().map_err(|_| ()))
                .collect::<Result<Vec<[u8; 32]>, ()>>()?;
            if !eip8288_mixed::valid_batch_claims(&claims) {
                return Err(());
            }
            let (program, proof) = decode_stark(&batch_hash(&claims), &key, r.blob()?)?;
            all.extend(claims.iter().map(|claim| leanstark_dependency(claim, &key)));
            leaves.push((claims, key, program, proof));
        }
    }
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
    let proof = aggregate_bounded(children, raw, leaves, &deps)?.to_bytes_without_deps();
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
    11
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
/// The number of u32 values `nlean_limits` writes.
const LIMITS: usize = 11;
/// # Safety
/// Output must hold eleven writable u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != LIMITS {
        return 0;
    }
    let limits = [
        MAX_BYTES as u32,
        MAX_DEPS as u32,
        MAX_INNER_PROOFS_PER_STEP as u32,
        (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
        MAX_CLAIMS_PER_BATCH_LEAF as u32,
        MAX_INSTRUCTIONS as u32,
        MAX_OPERAND,
        MAX_INPUT_BYTES as u32,
        MAX_MIXED_GUEST_BYTES as u32,
        eip8288_mixed::MAX_GENERIC_COMMITTED_LOG as u32,
        eip8288_mixed::MAX_GENERIC_BUS_LOG as u32,
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

/// Encodes a native aggregation input: direct `(dependency, witness)` pairs, recursive `(dependencies, proof)` children,
/// discards, then batch leaves `(verification key hash, claims, witness)`.
pub fn aggregation_input(
    direct: &[(Dep, &[u8])],
    children: &[(&[Dep], &[u8])],
    discards: &[Dep],
    leaves: &[([u8; 32], &[[u8; 32]], &[u8])],
) -> Vec<u8> {
    let mut out = Vec::new();
    put_number(&mut out, direct.len());
    for (dep, witness) in direct {
        out.extend_from_slice(dep);
        put_blob(&mut out, witness);
    }
    put_number(&mut out, children.len());
    for (deps, proof) in children {
        put_deps(&mut out, deps);
        put_blob(&mut out, proof);
    }
    put_deps(&mut out, discards);
    if !leaves.is_empty() {
        put_number(&mut out, leaves.len());
        for (key, claims, witness) in leaves {
            out.extend_from_slice(key);
            put_number(&mut out, claims.len());
            for claim in *claims {
                out.extend_from_slice(claim);
            }
            put_blob(&mut out, witness);
        }
    }
    out
}

/// Proves a leanSTARK witness of `source` for public statement `hash` and returns it with its verification key hash; a
/// batch leaf over `claims` uses `hash = batch_hash(claims)`.
pub fn prove_stark(source: &str, hash: &[u8; 32]) -> Result<(Vec<u8>, [u8; 32]), ()> {
    let _guard = begin_proof();
    let program = lean_compiler::compile(&lean_compiler::parse(source).map_err(|_| ())?);
    let key = eip8288_mixed::generic_verification_key(&program).map_err(|_| ())?;
    let code = encode_program(&program);
    let (proof, _) = in_arena_phase(|| cpu::prove(&program, pi(hash), 1)).map_err(|_| ())?;
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

    /// A leanVM program whose only output is the public statement `hash`.
    fn statement_source(hash: &[u8; 32]) -> String {
        let words: Vec<_> = hash
            .chunks_exact(8)
            .map(|word| u64::from_le_bytes(word.try_into().unwrap()))
            .collect();
        format!(
            "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = f192({}, {}, 0)\n    p[GEN] = f192({}, {}, 0)\n    return\n",
            words[0], words[1], words[2], words[3]
        )
    }

    /// A direct leanSTARK witness of `message` under a key of its own, and that key.
    fn singleton(message: &[u8; 32]) -> (Vec<u8>, [u8; 32]) {
        let statement = batch_hash(&[*message]);
        prove_stark(&statement_source(&statement), &statement).unwrap()
    }

    /// A verifier program accepting any statement, so one key can prove several claim lists.
    const ANY_STATEMENT: &str = "from snark_lib import *\ndef main():\n    x = HeapBuf(2)\n    x[1] = 3\n    x[GEN] = x[1] * x[1]\n    return\n";
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

    /// Expected digests computed in Python: leanSPHINCS-only lists from the EIP-8288 `get_deps_hash`
    /// pseudocode, lists with leanSTARK entries from the split-domain deviation.
    #[test]
    fn dependency_commitment_matches_eip_pseudocode_vectors() {
        let dep = |scheme: u8, data: [u8; 32], key: [u8; 32]| {
            let mut dep = [0; 96];
            dep[31] = scheme;
            dep[32..64].copy_from_slice(&data);
            dep[64..].copy_from_slice(&key);
            dep
        };
        let key = |start: u8| std::array::from_fn(|i| start + i as u8);
        let (a, b, c, d) = (
            dep(0x10, [7; 32], key(0)),
            dep(0x10, [7; 32], key(1)),
            dep(0x10, [1; 32], [0xff; 32]),
            dep(0x10, [0xa5; 32], [0x5a; 32]),
        );
        let (g1, g2) = (
            dep(0x11, [7; 32], key(0)),
            dep(0x11, [0xa5; 32], [0x5a; 32]),
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
            (
                vec![g1],
                "3ae64e624f1d16e19a7699b95f501134828652df566420f26a4f298b5bb54b9a",
            ),
            (
                vec![g2, a, g1, c],
                "ba1180b0f634d713dbf9b5b87e68a36786f550676d2b05d4f49f699eeb42e90f",
            ),
            (
                vec![g2, d],
                "b5a19f98eacfd5f1ac57b535e3aadbdfaf430698ebe563422ba883aa02f38adf",
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
        assert_ne!(commitment(&[a]), commitment(&[g1]));
        assert_ne!(commitment(&[d]), commitment(&[g2]));
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
        let mut small = [0; 32];
        small[0] = 7;
        small[16] = 9;
        for input in [small, [255; 32]] {
            let (proof, key) = singleton(&input);
            assert!(verify_stark(&input, &key, &proof));
            // EIP-8288: a direct proof states get_batch_hash([data_hash]), not the bare data_hash.
            let (bare, bare_key) = prove_stark(&statement_source(&input), &input).unwrap();
            assert!(!verify_stark(&input, &bare_key, &bare));
            let mut encoded = Reader { bytes: &proof };
            let code = encoded.blob().unwrap();
            // EIP-8288: vk_hash binds the program commitment, not a hash of the serialized code.
            assert_eq!(
                &eip8288_mixed::GENERIC_VK_DOMAIN[..],
                b"ethereum/8288/leanSTARK/vk/v2\0"
            );
            let program = stark_program(code).unwrap();
            assert_eq!(
                key,
                eip8288_mixed::generic_verification_key(&program).unwrap()
            );
            let code_key = keccak(&[b"ethereum/8288/leanSTARK/vk/v1\0".as_slice(), code].concat());
            assert!(!verify_stark(&input, &code_key, &proof));
            // Another program's code cannot stand in for the committed one.
            let (other, other_key) = singleton(&[0x42; 32]);
            let other_code = Reader { bytes: &other }.blob().unwrap();
            let mut substituted = Vec::new();
            put_blob(&mut substituted, other_code);
            substituted.extend_from_slice(encoded.bytes);
            assert!(!verify_stark(&input, &key, &substituted));
            assert!(!verify_stark(&input, &other_key, &substituted));
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
        // The scheme is bound: the same data and key under leanSTARK is another dependency.
        let mut restated = dep;
        restated[31] = 0x11;
        for proof in [&leaf, &root] {
            assert!(verify_aggregate(&commitment(&[restated]), proof).is_err());
        }
        let mut other_scheme = Vec::new();
        put_number(&mut other_scheme, 0);
        put_number(&mut other_scheme, 1);
        put_deps(&mut other_scheme, &[dep]);
        put_blob(&mut other_scheme, &leaf);
        put_deps(&mut other_scheme, &[restated]);
        // A discard outside the input union is rejected, even when it differs from an input only in its scheme.
        assert!(prove_aggregate(&commitment(&[]), &other_scheme).is_err());
        assert!(prove_aggregate(&commitment(&[dep]), &other_scheme).is_err());
        // Unknown schemes fail in direct, recursive and discard lists alike.
        for scheme in [0x00, 0x12] {
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
        assert_eq!(
            unsafe {
                nlean_verify_leanstark([0; 32].as_ptr(), [0; 32].as_ptr(), [0; 8].as_ptr(), 8)
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
    fn preflight_rejects_allocation_hints_and_excess_claims() {
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
        let mut deps = vec![[0u8; 96]; MAX_DEPS + 1];
        for (i, dep) in deps.iter_mut().enumerate() {
            dep[31] = 0x11;
            dep[62..64].copy_from_slice(&(i as u16).to_be_bytes());
        }
        let input = aggregation_input(&[], &[(&deps, &[0])], &[], &[]);
        assert!(decode_child(&deps, &[0]).is_err());
        // Lists past the bound, including 16-bit counter boundaries, are rejected rather than truncated.
        for count in [65_535usize, 65_536] {
            let mut long = vec![[0u8; 96]; count];
            for (i, dep) in long.iter_mut().enumerate() {
                dep[31] = 0x11;
                dep[60..64].copy_from_slice(&(i as u32).to_be_bytes());
            }
            assert!(decode_child(&long, &[0]).is_err());
            assert!(
                prove_aggregate(
                    &commitment(&long),
                    &aggregation_input(&[], &[(&long, &[0])], &[], &[])
                )
                .is_err()
            );
            assert!(
                prove_aggregate(&commitment(&long), &aggregation_input(&[], &[], &long, &[]))
                    .is_err()
            );
        }
        assert!(prove_aggregate(&commitment(&deps), &input).is_err());
    }
    /// A statement program padded to exactly `size` compiled instructions, support code included.
    fn sized_source(hash: &[u8; 32], size: usize) -> String {
        let base = statement_source(hash);
        for padding in (0..size).step_by(16) {
            let source = base.replace(
                "def main():\n",
                &format!("def main():\n    h = StackBuf({})\n    for i in unroll(0, {padding}):\n        h[i] = i + 1\n", padding.max(1)),
            );
            let length = lean_compiler::compile(&lean_compiler::parse(&source).unwrap())
                .prog
                .len();
            if length == size {
                return source;
            }
            assert!(length < size, "no padding compiles to {size} instructions");
        }
        panic!("no padding compiles to {size} instructions");
    }

    /// EIP-8288 Test Case 12: every leaf meets the verifier resource bounds, at direct admission and in aggregation, even
    /// when all its claims are discarded. Program size is the compiled, padded program, and the code's byte count is
    /// checked against it without truncation.
    #[test]
    fn resource_bounds_apply_to_discarded_leaves() {
        let claim = [0x31; 32];
        let statement = batch_hash(&[claim]);
        let (witness, key) =
            prove_stark(&sized_source(&statement, MAX_INSTRUCTIONS), &statement).unwrap();
        let mut reader = Reader { bytes: &witness };
        let code = reader.blob().unwrap();
        let proof = reader.bytes;
        assert_eq!(instruction_count(code), Ok(MAX_INSTRUCTIONS));
        assert!(verify_stark(&claim, &key, &witness));
        let dep = leanstark_dependency(&claim, &key);
        let empty = commitment(&[]);
        let discarded = |witness: &[u8]| {
            let as_leaf = aggregation_input(&[], &[], &[dep], &[(key, &[claim], witness)]);
            let as_direct = aggregation_input(&[(dep, witness)], &[], &[dep], &[]);
            [as_leaf, as_direct].map(|input| prove_aggregate(&empty, &input))
        };
        assert_eq!(discarded(&witness), [Ok(vec![]), Ok(vec![])]);
        let with_code = |count: usize, bytes: usize| {
            let mut code = Vec::new();
            put_number(&mut code, count);
            code.resize(4 + bytes, 0);
            let mut forged = Vec::new();
            put_blob(&mut forged, &code);
            forged.extend_from_slice(proof);
            forged
        };
        for (count, bytes) in [
            (2 * MAX_INSTRUCTIONS, 45 * 2 * MAX_INSTRUCTIONS),
            (MAX_INSTRUCTIONS, 45 * MAX_INSTRUCTIONS - 1),
            (MAX_INSTRUCTIONS, 45 * MAX_INSTRUCTIONS + 1),
            (1 << 31, 45 * MAX_INSTRUCTIONS),
        ] {
            let forged = with_code(count, bytes);
            assert!(!verify_stark(&claim, &key, &forged));
            assert!(discarded(&forged).iter().all(Result::is_err));
        }
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
        assert_eq!(nlean_abi_version(), 11);
        let mut key = [0u8; 32];
        assert_eq!(unsafe { nlean_aggregated_vk(key.as_mut_ptr()) }, 1);
        assert_eq!(
            key.iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>(),
            "11a02524eee37c856d80150f4c48a1636c5615f840e163968194b5cd1b49cc97"
        );
        let mut limits = [0u32; LIMITS];
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), LIMITS) }, 1);
        assert_eq!(
            limits,
            [
                MAX_BYTES as u32,
                MAX_DEPS as u32,
                MAX_INNER_PROOFS_PER_STEP as u32,
                (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
                MAX_CLAIMS_PER_BATCH_LEAF as u32,
                MAX_INSTRUCTIONS as u32,
                MAX_OPERAND,
                MAX_INPUT_BYTES as u32,
                MAX_MIXED_GUEST_BYTES as u32,
                eip8288_mixed::MAX_GENERIC_COMMITTED_LOG as u32,
                eip8288_mixed::MAX_GENERIC_BUS_LOG as u32
            ]
        );
        assert_eq!(unsafe { nlean_limits(std::ptr::null_mut(), LIMITS) }, 0);
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), LIMITS - 1) }, 0);
        let last_error = || {
            let mut message = [0u8; 64];
            let len = unsafe { nlean_last_error(message.as_mut_ptr(), message.len()) };
            String::from_utf8(message[..len].to_vec()).unwrap()
        };
        assert_eq!(checked(|| panic!("contained upstream failure")), -1);
        assert_eq!(last_error(), "contained upstream failure");
        let pool_panic = || {
            parallel::for_each(parallel::num_threads(), |_| panic!("pool task failed"));
            Ok(true)
        };
        assert_eq!(checked(pool_panic), -1);
        assert_eq!(last_error(), "pool task failed");
        assert_eq!(checked(|| Err(())), 0);
        assert_eq!(last_error(), "");
    }

    #[test]
    fn generic_input_order_does_not_change_canonical_dependencies() {
        let mut entries = Vec::new();
        for value in [7u8, 8] {
            let mut message = [0; 32];
            message[0] = value;
            message[16] = 9;
            let (witness, key) = singleton(&message);
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
            verify_aggregate(&commitment(&deps), &proof).unwrap();
            let noncanonical: Vec<_> = deps.iter().rev().copied().collect();
            assert!(verify_aggregate(&commitment(&noncanonical), &proof).is_err());
        }
    }

    #[test]
    fn recursive_generic_parent_compresses_large_inputs_and_preserves_discards() {
        let mut entries = Vec::new();
        for value in 7u8..23 {
            let mut message = [0; 32];
            message[0] = value;
            message[16] = 9;
            let (witness, key) = singleton(&message);
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
        verify_aggregate(&commitment(&deps), &parent).unwrap();

        let reaggregate = |retained: &[Dep], discards: &[Dep]| {
            let mut input = Vec::new();
            put_number(&mut input, 0);
            put_number(&mut input, 1);
            put_deps(&mut input, &deps);
            put_blob(&mut input, &parent);
            put_deps(&mut input, discards);
            assert!(input.len() < 1024 * 1024);
            let proof = prove_aggregate(&commitment(retained), &input).unwrap();
            verify_aggregate(&commitment(retained), &proof).unwrap();
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
        verify_aggregate(&commitment(&mixed_deps), &mixed).unwrap();
        assert!(mixed.len() <= MAX_MIXED_GUEST_BYTES);
        let mut discard = Vec::new();
        put_number(&mut discard, 0);
        put_number(&mut discard, 1);
        put_deps(&mut discard, &mixed_deps);
        put_blob(&mut discard, &mixed);
        put_deps(&mut discard, &[sphincs_dep]);
        let generic_only = prove_aggregate(&commitment(&deps), &discard).unwrap();
        verify_aggregate(&commitment(&deps), &generic_only).unwrap();
        eprintln!(
            "generic16 parent={} retained16={} retained1={} mixed={}",
            parent.len(),
            all.len(),
            one.len(),
            mixed.len()
        );
    }

    #[test]
    fn oversized_root_fails_closed() {
        let mut dep = [0; 96];
        dep[31] = 0x11;
        let hash = commitment(&[dep]);
        assert!(verify_aggregate(&hash, &vec![0; MAX_MIXED_GUEST_BYTES + 1]).is_err());
        assert!(!verify_stark(&[0; 32], &[0; 32], &vec![0; MAX_BYTES + 1]));
    }

    #[test]
    fn duplicate_generic_claims_compress_once_and_reject_invalid_origins() {
        let mut message = [0; 32];
        message[0] = 7;
        message[16] = 9;
        let statement = batch_hash(&[message]);
        let source = statement_source(&statement);
        let (first, key) = prove_stark(&source, &statement).unwrap();
        let program = lean_compiler::compile(&lean_compiler::parse(&source).unwrap());
        let mut second = Vec::new();
        put_blob(&mut second, &encode_program(&program));
        let proof = {
            let _guard = PROVER.lock().unwrap();
            cpu::prove(&program, pi(&statement), 2).unwrap().0
        };
        cpu::verify(&program, &pi(&statement), &proof).unwrap();
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
        verify_aggregate(&commitment(&[dep]), &result).unwrap();
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

    fn single_signature_input(seed: u8) -> ([u8; 32], Vec<u8>) {
        let (sk, pk) = sphincs::key_gen_from_seed([seed; 32]);
        let message = [seed; 32];
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
        (commitment(&[dep]), input)
    }

    #[test]
    fn verification_and_proving_do_not_share_arena_phases() {
        let (hash, input) = single_signature_input(44);
        let proof = prove_aggregate(&hash, &input).unwrap();
        let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let verified = std::sync::Arc::new(AtomicUsize::new(0));
        let verifier = {
            let (stop, verified, proof) = (stop.clone(), verified.clone(), proof.clone());
            std::thread::spawn(move || {
                let mut failures = 0;
                while !stop.load(Ordering::Acquire) {
                    failures += usize::from(verify_aggregate(&hash, &proof).is_err());
                    verified.fetch_add(1, Ordering::Release);
                }
                failures
            })
        };
        // Phase churn as a prover produces it, then a real proof whose arena buffers the verifier must not touch.
        while verified.load(Ordering::Acquire) < 20 {
            let _guard = begin_proof();
            for _ in 0..1000 {
                in_arena_phase(|| drop(zk_alloc::enter_phase()));
            }
        }
        let (other_hash, other_input) = single_signature_input(45);
        let other = prove_aggregate(&other_hash, &other_input);
        stop.store(true, Ordering::Release);
        assert_eq!(verifier.join().unwrap(), 0);
        verify_aggregate(&other_hash, &other.unwrap()).unwrap();
    }

    #[test]
    fn verification_does_not_wait_for_an_open_phase() {
        const PHASE: std::time::Duration = std::time::Duration::from_secs(4);
        let (hash, input) = single_signature_input(46);
        let proof = prove_aggregate(&hash, &input).unwrap();
        let opened = std::sync::Arc::new(Barrier::new(2));
        let prover = {
            let opened = opened.clone();
            std::thread::spawn(move || {
                let _guard = begin_proof();
                in_arena_phase(|| {
                    let _phase = zk_alloc::enter_phase();
                    opened.wait();
                    std::thread::sleep(PHASE);
                });
            })
        };
        opened.wait();
        let started = std::time::Instant::now();
        verify_aggregate(&hash, &proof).unwrap();
        let waited = started.elapsed();
        prover.join().unwrap();
        assert!(
            waited < PHASE / 2,
            "verification waited {waited:?} for the open phase"
        );
    }

    #[cfg(all(target_os = "linux", target_env = "gnu"))]
    #[test]
    fn glibc_settings_parse_like_strtoul() {
        assert_eq!(parse_c_integer("131072"), Some(131072));
        assert_eq!(parse_c_integer("0x40000"), Some(0x40000));
        assert_eq!(parse_c_integer("0100"), Some(64));
        assert_eq!(parse_c_integer("0"), Some(0));
        assert_eq!(parse_c_integer("4294967296"), Some(i32::MAX));
        assert_eq!(parse_c_integer("lots"), None);
    }

    #[cfg(all(target_os = "linux", target_env = "gnu"))]
    #[test]
    fn proving_leaves_host_heap_on_glibc_defaults() {
        drop(begin_proof());
        // Above glibc's 32 MiB dynamic mmap threshold cap, below a thread arena's 64 MiB heap: only M_MMAP_MAX = 0 keeps it off mmap.
        let before = unsafe { libc::mallinfo2() }.hblks;
        let buffer = std::hint::black_box(vec![1u8; 40 << 20]);
        assert!(unsafe { libc::mallinfo2() }.hblks > before);
        drop(buffer);
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

    /// EIP-8288 Test Cases 2 and 12: a batch leaf and singleton proofs under one key cover the same claims.
    #[test]
    fn batch_leaf_and_singletons_prove_the_same_dependencies() {
        let witness =
            |claims: &[[u8; 32]]| prove_stark(ANY_STATEMENT, &batch_hash(claims)).unwrap();
        let (a, b, c) = ([0xa0; 32], [0xb0; 32], [0xc0; 32]);
        let (leaf, key) = witness(&[a, b]);
        let (single_a, key_a) = witness(&[a]);
        let (single_b, _) = witness(&[b]);
        assert_eq!(key, key_a);
        let deps = [
            leanstark_dependency(&a, &key),
            leanstark_dependency(&b, &key),
        ];
        let hash = commitment(&deps);
        assert!(verify_batch_leaf(&[a, b], &key, &leaf));
        assert!(!verify_batch_leaf(&[b, a], &key, &leaf));
        assert!(!verify_batch_leaf(&[a], &key, &leaf));
        let batched = prove_aggregate(
            &hash,
            &aggregation_input(&[], &[], &[], &[(key, &[a, b], &leaf)]),
        )
        .unwrap();
        let direct = [(deps[1], &single_b[..]), (deps[0], &single_a[..])];
        let singles = prove_aggregate(&hash, &aggregation_input(&direct, &[], &[], &[])).unwrap();
        for proof in [&batched, &singles] {
            verify_aggregate(&hash, proof).unwrap();
        }
        // One singleton alone, or a proof under another key, does not satisfy both claims.
        assert!(prove_aggregate(&hash, &aggregation_input(&direct[..1], &[], &[], &[])).is_err());
        let (other, other_key) = singleton(&a);
        assert!(
            prove_aggregate(
                &hash,
                &aggregation_input(&[(deps[0], &other)], &[], &[], &[])
            )
            .is_err()
        );
        assert!(
            prove_aggregate(
                &hash,
                &aggregation_input(&[], &[], &[], &[(other_key, &[a, b], &leaf)])
            )
            .is_err()
        );
        // The leaf statement binds the exact nonempty, bounded, strictly increasing claim list.
        let (over_witness, _) = witness(&[[1; 32]; MAX_CLAIMS_PER_BATCH_LEAF + 1]);
        let over: Vec<[u8; 32]> = (1..=MAX_CLAIMS_PER_BATCH_LEAF as u8 + 1)
            .map(|i| [i; 32])
            .collect();
        // Aggregation rejects an unsorted or repeated list whose digest the leaf program proves; the host check rejects it
        // here, and the guest's own check is covered in rec_aggregation.
        for claims in [&[b, a][..], &[a, a]] {
            let (proof, _) = witness(claims);
            assert!(
                prove_aggregate(
                    &hash,
                    &aggregation_input(&[], &[], &[], &[(key, claims, &proof)])
                )
                .is_err()
            );
        }
        for (claims, proof) in [
            (&[b, a][..], &leaf),
            (&[a, a], &leaf),
            (&[], &leaf),
            (&[a], &leaf),
            (&[a, b, c], &leaf),
            (&over, &over_witness),
        ] {
            assert!(
                prove_aggregate(
                    &hash,
                    &aggregation_input(&[], &[], &[], &[(key, claims, proof)])
                )
                .is_err()
            );
        }
        // Union with an overlapping leaf, then discard a: {b, c} is retained; the leaf alone cannot restore c.
        let (bc, _) = witness(&[b, c]);
        let retained = [deps[1], leanstark_dependency(&c, &key)];
        let leaves = [(key, &[a, b][..], &leaf[..]), (key, &[b, c], &bc)];
        let pruned = prove_aggregate(
            &commitment(&retained),
            &aggregation_input(&[], &[], &deps[..1], &leaves),
        )
        .unwrap();
        verify_aggregate(&commitment(&retained), &pruned).unwrap();
        let only_a = prove_aggregate(
            &commitment(&deps[..1]),
            &aggregation_input(&[], &[], &deps[1..], &leaves[..1]),
        )
        .unwrap();
        let restore = aggregation_input(&[], &[(&deps[..1], &only_a)], &[], &[]);
        assert!(prove_aggregate(&hash, &restore).is_err());
    }

    /// EIP-8288 `MAX_INNER_PROOFS_PER_STEP` counts direct leanSTARK proofs, batch leaves and recursive proofs together.
    #[test]
    fn inner_proofs_per_step_count_every_proof_kind() {
        let (witness, key) = singleton(&[1; 32]);
        let dep = leanstark_dependency(&[1; 32], &key);
        let parent = prove_aggregate(
            &commitment(&[dep]),
            &aggregation_input(&[(dep, &witness)], &[], &[], &[]),
        )
        .unwrap();
        let (leaf, leaf_key) = prove_stark(ANY_STATEMENT, &batch_hash(&[[2; 32]])).unwrap();
        let discarded = leanstark_dependency(&[2; 32], &leaf_key);
        let step = |children: usize| {
            let deps = [dep];
            let children = vec![(&deps[..], &parent[..]); children];
            let input = aggregation_input(
                &[(dep, &witness)],
                &children,
                &[discarded],
                &[(leaf_key, &[[2; 32]], &leaf)],
            );
            prove_aggregate(&commitment(&[dep]), &input)
        };
        assert_eq!(step(MAX_INNER_PROOFS_PER_STEP - 2).unwrap(), parent);
        assert!(step(MAX_INNER_PROOFS_PER_STEP - 1).is_err());
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
