// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Pinned leanVM/SPHINCS verification. Generic STARKs are carried and reverified;
//! the upstream Ethereum guest recursively compresses SPHINCS claims.

use bincode::Options;
use leanvm_core::cpu::{self, Program};
use primitives::field::F192;
use rec_aggregation::{ClaimSelection, EthereumProof, SignatureClaims};
use sphincs::{SphincsPublicKey, SphincsSignature};
use std::cell::Cell;
use std::collections::{BTreeSet, HashMap, HashSet};
use std::sync::{Mutex, OnceLock};
use tiny_keccak::{Hasher, Keccak};

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
const MAX_DEPS: usize = 4096;
const MAX_INSTRUCTIONS: usize = 16384;
const MAX_OPERAND: u32 = 65535;
const MAX_GENERIC_STARKS: usize = 16;
const MAX_DECODE_BYTES: usize = 32 * 1024 * 1024;
const MAGIC: &[u8; 4] = b"NLR2";
static PROVER: Mutex<()> = Mutex::new(());
type Dep = [u8; 96];

pub fn keccak(bytes: &[u8]) -> [u8; 32] {
    let mut h = Keccak::v256();
    h.update(bytes);
    let mut out = [0; 32];
    h.finalize(&mut out);
    out
}

pub fn aggregated_vk() -> [u8; 32] {
    static KEY: OnceLock<[u8; 32]> = OnceLock::new();
    *KEY.get_or_init(|| {
        let cells = cpu::fs_seed(rec_aggregation::aggregation::unified_guest());
        [
            cells[0].c0.to_le_bytes(),
            cells[0].c1.to_le_bytes(),
            cells[1].c0.to_le_bytes(),
            cells[1].c1.to_le_bytes(),
        ]
        .concat()
        .try_into()
        .unwrap()
    })
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
    fn ethereum(&mut self) -> Result<(), ()> {
        self.vector(0, 1)?; // XMSS is unsupported
        self.vector(MAX_DEPS, 64)?; // public key + message
        self.vector(0, 32)?; // DA commitments are unsupported
        self.vector(32, 24)?;
        self.vector(32, 24)?;
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
fn verify_stark(hash: &[u8; 32], vk: &[u8; 32], witness: &[u8]) -> bool {
    let result = || -> Result<(), ()> {
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
        cpu::verify(&program, &pi(hash), &proof).map_err(|_| ())?;
        Ok(())
    };
    result().is_ok()
}

struct Aggregate {
    deps: Vec<Dep>,
    sphincs: Option<EthereumProof>,
    starks: HashMap<Dep, Vec<u8>>,
}
fn decode_aggregate(bytes: &[u8]) -> Result<Aggregate, ()> {
    decode_aggregate_with_hash(bytes, None)
}
fn decode_aggregate_with_hash(bytes: &[u8], expected: Option<&[u8; 32]>) -> Result<Aggregate, ()> {
    let mut r = Reader { bytes };
    if r.take(4)? != MAGIC {
        return Err(());
    }
    let deps = r.deps()?;
    if !deps.windows(2).all(|w| w[0] < w[1]) {
        return Err(());
    }
    if deps.iter().filter(|d| d[31] == 0x11).count() > MAX_GENERIC_STARKS {
        return Err(());
    }
    if expected.is_some_and(|hash| commitment(&deps) != *hash) {
        return Err(());
    }
    let proof = r.blob()?;
    let sphincs = if proof.is_empty() {
        None
    } else {
        let mut preflight = Preflight::new(proof);
        preflight.ethereum()?;
        preflight.end()?;
        Some(EthereumProof::from_bytes(proof).map_err(|_| ())?)
    };
    let mut starks = HashMap::new();
    let stark_count = r.count()?;
    if stark_count > MAX_GENERIC_STARKS {
        return Err(());
    }
    for _ in 0..stark_count {
        let dep = r.dep()?;
        if dep[31] != 0x11 || starks.insert(dep, r.blob()?.to_vec()).is_some() {
            return Err(());
        }
    }
    r.end()?;
    let expected: BTreeSet<_> = deps.iter().filter(|d| d[31] == 0x10).copied().collect();
    match &sphincs {
        Some(p) => {
            if !p.xmss_signers().is_empty()
                || !p.da_commitments().is_empty()
                || p.sphincs_signers()
                    .iter()
                    .map(|(pk, m)| sphincs_dependency(pk, m))
                    .collect::<BTreeSet<_>>()
                    != expected
            {
                return Err(());
            }
            p.verify().map_err(|_| ())?;
        }
        None if !expected.is_empty() => return Err(()),
        None => (),
    }
    let expected_starks: HashSet<_> = deps.iter().filter(|d| d[31] == 0x11).copied().collect();
    if starks.keys().copied().collect::<HashSet<_>>() != expected_starks {
        return Err(());
    }
    for (d, w) in &starks {
        if !verify_stark(
            d[32..64].try_into().unwrap(),
            d[64..96].try_into().unwrap(),
            w,
        ) {
            return Err(());
        }
    }
    Ok(Aggregate {
        deps,
        sphincs,
        starks,
    })
}

pub fn prove_aggregate(hash: &[u8; 32], input: &[u8]) -> Result<Vec<u8>, ()> {
    let _guard = PROVER
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    leanvm_core::init_prover_pool();
    let mut r = Reader { bytes: input };
    let mut all = Vec::new();
    let mut raw = Vec::new();
    let mut claims = HashMap::new();
    let mut starks = HashMap::new();
    for _ in 0..r.count()? {
        let d = r.dep()?;
        let w = r.blob()?;
        if d[31] == 0x10 {
            let m = d[32..64].try_into().map_err(|_| ())?;
            let (pk, sig) = parse_sphincs(&m, d[64..96].try_into().map_err(|_| ())?, w)?;
            claims.insert(d, (pk, m));
            raw.push((pk, m, sig));
        } else {
            if !verify_stark(
                d[32..64].try_into().unwrap(),
                d[64..96].try_into().unwrap(),
                w,
            ) {
                return Err(());
            }
            starks.insert(d, w.to_vec());
        }
        all.push(d);
    }
    let child_count = r.count()?;
    if child_count > rec_aggregation::MAX_RECURSIONS {
        return Err(());
    }
    let mut children = Vec::new();
    for _ in 0..child_count {
        let deps = r.deps()?;
        let child = decode_aggregate(r.blob()?)?;
        if child.deps
            != deps
                .iter()
                .copied()
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect::<Vec<_>>()
        {
            return Err(());
        }
        all.extend(deps);
        starks.extend(child.starks);
        if let Some(p) = child.sphincs {
            for (pk, m) in p.sphincs_signers() {
                claims.insert(sphincs_dependency(pk, m), (*pk, *m));
            }
            children.push(p);
        }
    }
    let discards: HashSet<_> = r.deps()?.into_iter().collect();
    r.end()?;
    let mut seen = HashSet::new();
    let deps: Vec<_> = all
        .into_iter()
        .filter(|d| !discards.contains(d) && seen.insert(*d))
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    if deps.len() > MAX_DEPS
        || deps.iter().filter(|d| d[31] == 0x11).count() > MAX_GENERIC_STARKS
        || commitment(&deps) != *hash
    {
        return Err(());
    }
    let signatures = SignatureClaims {
        xmss: vec![],
        sphincs: deps
            .iter()
            .filter(|d| d[31] == 0x10)
            .map(|d| claims.get(d).copied().ok_or(()))
            .collect::<Result<BTreeSet<_>, _>>()?
            .into_iter()
            .collect(),
    };
    let proof = if signatures.sphincs.is_empty() {
        vec![]
    } else {
        rec_aggregation::aggregate(
            &children,
            vec![],
            raw,
            &[],
            Some(ClaimSelection {
                signatures: &signatures,
                da_commitments: &[],
            }),
            1,
        )
        .map_err(|_| ())?
        .to_bytes()
    };
    let mut out = MAGIC.to_vec();
    put_deps(&mut out, &deps);
    put_blob(&mut out, &proof);
    let retained: Vec<_> = deps.iter().filter(|d| d[31] == 0x11).collect();
    put_number(&mut out, retained.len());
    for d in retained {
        out.extend_from_slice(d);
        put_blob(&mut out, starks.get(d).ok_or(())?);
    }
    if out.len() > MAX_BYTES {
        return Err(());
    }
    Ok(out)
}

// All ABI entry points reject null/oversized buffers and contain upstream panics.
unsafe fn bytes<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], ()> {
    if len > MAX_BYTES || (ptr.is_null() && len != 0) {
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
    thread_local! { static IN_ABI: Cell<bool> = const { Cell::new(false) }; }
    HOOK.get_or_init(|| {
        let previous = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            if !IN_ABI.with(Cell::get) {
                previous(info);
            }
        }));
    });
    struct CallGuard(bool);
    impl Drop for CallGuard {
        fn drop(&mut self) {
            IN_ABI.with(|flag| flag.set(self.0));
        }
    }
    let _guard = CallGuard(IN_ABI.with(|flag| flag.replace(true)));
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f))
        .ok()
        .and_then(Result::ok)
        .unwrap_or(false) as i32
}
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    3
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
        (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
        MAX_GENERIC_STARKS as u32,
        MAX_INSTRUCTIONS as u32,
        MAX_OPERAND,
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
            unsafe { bytes(input, input_len)? },
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
    leanvm_core::init_prover_pool();
    let program = lean_compiler::compile(&lean_compiler::parse(source).map_err(|_| ())?);
    let code = encode_program(&program);
    let (proof, _) = cpu::prove(&program, pi(hash), 1).map_err(|_| ())?;
    let mut out = Vec::new();
    put_blob(&mut out, &code);
    out.extend(wire().serialize(&proof).map_err(|_| ())?);
    Ok((out, keccak(&code)))
}

#[cfg(test)]
mod tests {
    use super::*;
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
            sphincs::sign(&sk, &message).unwrap().to_bytes().as_slice(),
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
        let source = "from snark_lib import *\ndef main():\n    p = GEN ** 0\n    p[1] = 7\n    p[GEN] = 9\n    return\n";
        let mut input = [0; 32];
        input[0] = 7;
        input[16] = 9;
        let (proof, key) = prove_stark(source, &input).unwrap();
        assert!(verify_stark(&input, &key, &proof));
        assert!(!verify_stark(&[0; 32], &key, &proof));
        assert!(!verify_stark(&input, &[0; 32], &proof));
        let mut bad = proof;
        *bad.last_mut().unwrap() ^= 1;
        assert!(!verify_stark(&input, &key, &bad));
    }
    #[test]
    fn real_recursive_composition_and_discards_bind_full_dependency_set() {
        let (sk, pk) = sphincs::key_gen_from_seed([42; 32]);
        let message = [7; 32];
        let signature = [
            pk.flatten().as_slice(),
            sphincs::sign(&sk, &message).unwrap().to_bytes().as_slice(),
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
        // Reject malicious nested vector lengths before upstream claim reconstruction.
        for preceding_vectors in 0..7 {
            let mut nested = vec![0; preceding_vectors * 8];
            nested.extend_from_slice(&u64::MAX.to_le_bytes());
            let mut envelope = MAGIC.to_vec();
            put_deps(&mut envelope, &[]);
            put_blob(&mut envelope, &nested);
            put_number(&mut envelope, 0);
            assert!(decode_aggregate(&envelope).is_err());
        }
        let (_, pk) = sphincs::key_gen_from_seed([42; 32]);
        let claims = SignatureClaims {
            xmss: vec![],
            sphincs: vec![(pk, [7; 32]); MAX_DEPS + 1],
        };
        let core = (
            Vec::<[u8; 32]>::new(),
            Vec::<F192>::new(),
            Vec::<F192>::new(),
            cpu::Proof {
                stream: vec![],
                merkle: vec![],
            },
        );
        let mut envelope = MAGIC.to_vec();
        put_deps(&mut envelope, &[]);
        put_blob(&mut envelope, &wire().serialize(&(claims, core)).unwrap());
        put_number(&mut envelope, 0);
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
        let mut limits = [0u32; 7];
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 7) }, 1);
        assert_eq!(
            limits,
            [
                MAX_BYTES as u32,
                MAX_DEPS as u32,
                rec_aggregation::MAX_RECURSIONS as u32,
                (sphincs::PUB_KEY_SIZE + sphincs::SIG_SIZE) as u32,
                MAX_GENERIC_STARKS as u32,
                MAX_INSTRUCTIONS as u32,
                MAX_OPERAND
            ]
        );
        assert_eq!(unsafe { nlean_limits(std::ptr::null_mut(), 7) }, 0);
        assert_eq!(unsafe { nlean_limits(limits.as_mut_ptr(), 6) }, 0);
        assert_eq!(checked(|| panic!("contained upstream failure")), 0);
    }
}
