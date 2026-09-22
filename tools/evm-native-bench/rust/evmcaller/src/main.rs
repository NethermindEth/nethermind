//! Drives Nethermind's C# EVM from Rust, two ways:
//!   * the EVM owning its own in-memory state (MemDb + trie), and
//!   * the EVM borrowing state from Rust through three C callbacks — the rbuilder design.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Barrier};
use std::time::Instant;

type Handle = *mut std::ffi::c_void;

#[link(name = "Nethermind.Evm.Native")]
extern "C" {
    fn nm_evm_engine_create(seed: i32) -> Handle;
    fn nm_evm_engine_create_cb(
        seed: i32,
        get_account: extern "C" fn(*const u8, *mut u8, *mut u8, *mut u8) -> i32,
        get_storage: extern "C" fn(*const u8, *const u8, *mut u8) -> i32,
        get_code: extern "C" fn(*const u8, *mut u8, i32) -> i32,
    ) -> Handle;
    fn nm_evm_engine_run(handle: Handle, count: i32, workload: i32) -> i64;
    fn nm_evm_engine_reads(handle: Handle, a: *mut i64, s: *mut i64, c: *mut i64);
    fn nm_evm_engine_destroy(handle: Handle);
    fn nm_evm_gc_stats(a: *mut i64, g0: *mut i32, g1: *mut i32, g2: *mut i32, h: *mut i64);
}

/// Counting callbacks from every thread on one cache line serialises them and destroys the
/// scaling measurement; the per-engine counters on the managed side already report reads/tx.
static ACCOUNT_CALLS: AtomicU64 = AtomicU64::new(0);

const LOOP_CODE: [u8; 15] = [
    0x60, 0x00, 0x5b, 0x60, 0x01, 0x01, 0x80, 0x61, 0x27, 0x10, 0x11, 0x60, 0x02, 0x57, 0x00,
];

fn loop_code_hash() -> [u8; 32] {
    use tiny_keccak::{Hasher, Keccak};
    let mut k = Keccak::v256();
    let mut out = [0u8; 32];
    k.update(&LOOP_CODE);
    k.finalize(&mut out);
    out
}

/// keccak256("") — an account with no code must report this or the EVM goes looking for code.
const EMPTY_CODE_HASH: [u8; 32] = [
    0xc5, 0xd2, 0x46, 0x01, 0x86, 0xf7, 0x23, 0x3c, 0x92, 0x7e, 0x7d, 0xb2, 0xdc, 0xc7, 0x03, 0xc0,
    0xe5, 0x00, 0xb6, 0x53, 0xca, 0x82, 0x27, 0x3b, 0x7b, 0xfa, 0xd8, 0x04, 0x5d, 0x85, 0xa4, 0x70,
];

/// The host's state: one funded sender. Everything else does not exist.
extern "C" fn get_account(addr: *const u8, nonce: *mut u8, balance: *mut u8, code_hash: *mut u8) -> i32 {
    let a = unsafe { std::slice::from_raw_parts(addr, 20) };
    unsafe {
        std::ptr::write_bytes(nonce, 0, 8);
        std::ptr::write_bytes(balance, 0, 32);
    }
    if a[0] == 0x11 && a[19] == 0x11 {
        let bal = 1_000_000_000_000_000_000_000_000u128.to_le_bytes();
        unsafe {
            std::ptr::copy_nonoverlapping(bal.as_ptr(), balance, 16);
            std::ptr::copy_nonoverlapping(EMPTY_CODE_HASH.as_ptr(), code_hash, 32);
        }
        return 1;
    }
    if a[0] == 0x33 && a[19] == 0x33 {
        unsafe { std::ptr::copy_nonoverlapping(LOOP_CODE_HASH.as_ptr(), code_hash, 32) };
        return 1;
    }
    0
}

extern "C" fn get_storage(_addr: *const u8, _key: *const u8, value: *mut u8) -> i32 {
    unsafe { std::ptr::write_bytes(value, 0, 32) };
    0
}

extern "C" fn get_code(hash: *const u8, buf: *mut u8, len: i32) -> i32 {
    let h = unsafe { std::slice::from_raw_parts(hash, 32) };
    if h == LOOP_CODE_HASH.as_slice() && len >= LOOP_CODE.len() as i32 {
        unsafe { std::ptr::copy_nonoverlapping(LOOP_CODE.as_ptr(), buf, LOOP_CODE.len()) };
        return LOOP_CODE.len() as i32;
    }
    -1
}

extern "C" fn segv_handler(_s: i32, info: *mut libc::siginfo_t, _c: *mut libc::c_void) {
    let addr = unsafe { (*info).si_addr() };
    eprintln!("[rust] OUR SIGSEGV handler fired at {addr:?}");
    std::process::exit(99);
}

fn install_sigsegv_handler() {
    unsafe {
        let mut sa: libc::sigaction = std::mem::zeroed();
        sa.sa_sigaction = segv_handler as *const () as usize;
        sa.sa_flags = libc::SA_SIGINFO | libc::SA_ONSTACK;
        libc::sigemptyset(&mut sa.sa_mask);
        libc::sigaction(libc::SIGSEGV, &sa, std::ptr::null_mut());
    }
}

fn pct(lat: &[f64], p: f64) -> f64 { lat[((lat.len() as f64) * p) as usize] }

fn gc() -> (i64, i32, i32, i32) {
    let (mut a, mut g0, mut g1, mut g2, mut h) = (0i64, 0i32, 0i32, 0i32, 0i64);
    unsafe { nm_evm_gc_stats(&mut a, &mut g0, &mut g1, &mut g2, &mut h) };
    let _ = h;
    (a, g0, g1, g2)
}

fn bench(label: &str, threads: usize, per_thread: usize, workload: i32, gas: f64, callbacks: bool) {
    let barrier = Arc::new(Barrier::new(threads));
    let (a0, c0a, c1a, c2a) = gc();

    let handles: Vec<_> = (0..threads)
        .map(|t| {
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                let engine = unsafe {
                    if callbacks {
                        nm_evm_engine_create_cb(t as i32 + 1, get_account, get_storage, get_code)
                    } else {
                        nm_evm_engine_create(t as i32 + 1)
                    }
                };
                assert!(!engine.is_null(), "engine {t} failed");
                let w = unsafe { nm_evm_engine_run(engine, 200, workload) };
                assert!(w >= 0, "warm-up returned {w}");
                barrier.wait();

                let mut lat = Vec::with_capacity(per_thread);
                let t0 = Instant::now();
                for _ in 0..per_thread {
                    let s = Instant::now();
                    let r = unsafe { nm_evm_engine_run(engine, 1, workload) };
                    lat.push(s.elapsed().as_secs_f64() * 1e6);
                    assert!(r >= 0, "run returned {r}");
                }
                let busy = t0.elapsed().as_secs_f64();
                let (mut ra, mut rs, mut rc) = (0i64, 0i64, 0i64);
                unsafe { nm_evm_engine_reads(engine, &mut ra, &mut rs, &mut rc) };
                unsafe { nm_evm_engine_destroy(engine) };
                lat.sort_by(|a, b| a.partial_cmp(b).unwrap());
                (lat, busy, ra)
            })
        })
        .collect();

    let res: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();
    let (a1, c0b, c1b, c2b) = gc();

    let total = threads * per_thread;
    let busy = res.iter().map(|(_, b, _)| *b).fold(0.0, f64::max);
    let mean = res.iter().map(|(l, _, _)| l.iter().sum::<f64>() / l.len() as f64).sum::<f64>() / threads as f64;
    let p50 = res.iter().map(|(l, _, _)| pct(l, 0.50)).sum::<f64>() / threads as f64;
    let p99 = res.iter().map(|(l, _, _)| pct(l, 0.99)).fold(0.0, f64::max);
    let max = res.iter().map(|(l, _, _)| *l.last().unwrap()).fold(0.0, f64::max);
    let reads: i64 = res.iter().map(|(_, _, r)| *r).sum();
    let tps = total as f64 / busy;

    println!(
        "{label:<34} {threads:>2}t | {:9.0} tx/s | mean {:8.2} p50 {:7.2} p99 {:7.2} max {:9.2} us | {:5.2} reads/tx | alloc {:6.0} B/tx | gc {}/{}/{}",
        tps, mean, p50, p99, max,
        reads as f64 / total as f64,
        (a1 - a0) as f64 / total as f64,
        c0b - c0a, c1b - c1a, c2b - c2a
    );
}

static LOOP_CODE_HASH: std::sync::LazyLock<[u8; 32]> = std::sync::LazyLock::new(loop_code_hash);

fn main() {
    install_sigsegv_handler();
    std::sync::LazyLock::force(&LOOP_CODE_HASH);
    println!("cpus: {}\n", std::thread::available_parallelism().map(|p| p.get()).unwrap_or(0));

    println!("=== A. state inside the EVM (MemDb + trie) ===");
    bench("transfer, same recipient", 1, 20_000, 0, 21_000.0, false);
    bench("transfer, fresh recipient each tx", 1, 20_000, 2, 21_000.0, false);
    bench("10k-iteration loop call", 1, 2_000, 1, 311_000.0, false);

    println!("\n=== B. state in Rust, read through callbacks ===");
    bench("transfer, same recipient", 1, 20_000, 0, 21_000.0, true);
    bench("transfer, fresh recipient each tx", 1, 20_000, 2, 21_000.0, true);
    bench("10k-iteration loop call", 1, 2_000, 1, 311_000.0, true);

    println!();
    println!("=== C. scaling, matched workloads ===");
    for t in [1usize, 2, 4, 8] {
        bench("A memdb   fresh recipient", t, 20_000, 2, 21_000.0, false);
    }
    println!();
    for t in [1usize, 2, 4, 8] {
        bench("B callbk  fresh recipient", t, 20_000, 2, 21_000.0, true);
    }
    println!();
    for t in [1usize, 8] {
        bench("A memdb   loop call", t, 2_000, 1, 311_000.0, false);
        bench("B callbk  loop call", t, 2_000, 1, 311_000.0, true);
    }
}
