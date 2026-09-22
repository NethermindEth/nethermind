//! revm baseline: the same two workloads the Nethermind EVM ran, same box, Prague.

use revm::bytecode::Bytecode;
use revm::database::{CacheDB, EmptyDB};
use revm::primitives::hardfork::SpecId;
use revm::primitives::{Address, Bytes, TxKind, U256};
use revm::state::AccountInfo;
use revm::{Context, ExecuteCommitEvm, MainBuilder, MainContext};
use std::time::Instant;

const LOOP_CODE: &[u8] = &[
    0x60, 0x00, // PUSH1 0
    0x5b, // JUMPDEST
    0x60, 0x01, // PUSH1 1
    0x01, // ADD
    0x80, // DUP1
    0x61, 0x27, 0x10, // PUSH2 10000
    0x11, // GT
    0x60, 0x02, // PUSH1 2
    0x57, // JUMPI
    0x00, // STOP
];

fn pct(lat: &[f64], p: f64) -> f64 { lat[((lat.len() as f64) * p) as usize] }

fn main() {
    let sender = Address::with_last_byte(0x11);
    let recipient = Address::with_last_byte(0x22);
    let contract = Address::with_last_byte(0x33);

    let mut db = CacheDB::new(EmptyDB::default());
    db.insert_account_info(
        sender,
        AccountInfo { balance: U256::from(10).pow(U256::from(24)), nonce: 0, ..Default::default() },
    );
    let code = Bytecode::new_raw(Bytes::from_static(LOOP_CODE));
    db.insert_account_info(
        contract,
        AccountInfo { balance: U256::ZERO, nonce: 0, code_hash: code.hash_slow(), code: Some(code), ..Default::default() },
    );

    let mut evm = Context::mainnet()
        .with_db(db)
        .modify_cfg_chained(|c| c.spec = SpecId::PRAGUE)
        .build_mainnet();

    let mut nonce: u64 = 0;

    for (name, to, gas_limit, n, gas_per_tx) in [
        ("value transfer          ", TkKind(recipient), 100_000u64, 20_000usize, 21_000.0f64),
        ("10k-iteration loop call ", TkKind(contract), 1_000_000u64, 2_000usize, 311_000.0f64),
    ] {
        let mut once = |evm: &mut _, nonce: &mut u64| {
            let tx = revm::context::TxEnv {
                caller: sender,
                kind: TxKind::Call(to.0),
                value: if gas_limit == 100_000 { U256::from(1) } else { U256::ZERO },
                gas_limit,
                gas_price: 1,
                nonce: *nonce,
                ..Default::default()
            };
            *nonce += 1;
            ExecuteCommitEvm::transact_commit(evm, tx).expect("transact")
        };

        for _ in 0..500 { let _ = once(&mut evm, &mut nonce); }

        let mut lat = Vec::with_capacity(n);
        let wall = Instant::now();
        for _ in 0..n {
            let t = Instant::now();
            let r = once(&mut evm, &mut nonce);
            lat.push(t.elapsed().as_secs_f64() * 1e6);
            std::hint::black_box(&r);
        }
        let wall = wall.elapsed().as_secs_f64();
        lat.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let tps = n as f64 / wall;
        println!(
            "revm 1t | {name} | {:9.0} tx/s | {:7.1} Mgas/s | mean {:7.2} p50 {:7.2} p99 {:7.2} p999 {:8.2} max {:9.2} us",
            tps, tps * gas_per_tx / 1e6,
            lat.iter().sum::<f64>() / n as f64,
            pct(&lat, 0.50), pct(&lat, 0.99), pct(&lat, 0.999), lat[n - 1]
        );
    }
}

struct TkKind(Address);
