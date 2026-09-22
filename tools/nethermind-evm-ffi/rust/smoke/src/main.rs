//! Exercises the whole C ABI against a toy host state: a funded sender and one contract that
//! writes storage and emits a log. Builds and runs with nothing but the bundle — no .NET SDK,
//! no Nethermind checkout.

use nethermind_evm_sys::*;
use std::collections::HashMap;
use std::ffi::c_void;
use tiny_keccak::{Hasher, Keccak};

/// SSTORE(1, 42); LOG1(topic 0xAA, no data); STOP
const CONTRACT: [u8; 13] = [
    0x60, 0x2a, // PUSH1 42     value
    0x60, 0x01, // PUSH1 1      key
    0x55, //       SSTORE
    0x60, 0xaa, // PUSH1 0xAA   topic
    0x60, 0x00, // PUSH1 0      size
    0x60, 0x00, // PUSH1 0      offset
    0xa1, //       LOG1
    0x00, //       STOP
];

const SENDER: [u8; 20] = [0x11; 20];
const CONTRACT_ADDR: [u8; 20] = [0x33; 20];
const RECIPIENT: [u8; 20] = [0x22; 20];

fn keccak(bytes: &[u8]) -> [u8; 32] {
    let mut k = Keccak::v256();
    let mut out = [0u8; 32];
    k.update(bytes);
    k.finalize(&mut out);
    out
}

/// The state the engine borrows. A real host would be rbuilder's `State<DB>`.
struct Host {
    accounts: HashMap<[u8; 20], (u64, [u8; 32], [u8; 32])>, // nonce, balance LE, code hash
    code: HashMap<[u8; 32], Vec<u8>>,
    account_calls: u64,
    storage_calls: u64,
    code_calls: u64,
}

unsafe extern "C" fn get_account(
    ctx: *mut c_void,
    address: *const u8,
    out_nonce: *mut u64,
    out_balance: *mut u8,
    out_code_hash: *mut u8,
) -> i32 {
    let host = &mut *(ctx as *mut Host);
    host.account_calls += 1;
    let addr: [u8; 20] = std::slice::from_raw_parts(address, 20).try_into().unwrap();
    match host.accounts.get(&addr) {
        Some((nonce, balance, code_hash)) => {
            *out_nonce = *nonce;
            std::ptr::copy_nonoverlapping(balance.as_ptr(), out_balance, 32);
            std::ptr::copy_nonoverlapping(code_hash.as_ptr(), out_code_hash, 32);
            1
        }
        None => 0,
    }
}

unsafe extern "C" fn get_storage(
    ctx: *mut c_void,
    _address: *const u8,
    _key: *const u8,
    out_value: *mut u8,
) -> i32 {
    let host = &mut *(ctx as *mut Host);
    host.storage_calls += 1;
    std::ptr::write_bytes(out_value, 0, 32);
    0 // every slot starts empty
}

unsafe extern "C" fn get_code(
    ctx: *mut c_void,
    code_hash: *const u8,
    out_buf: *mut u8,
    buf_len: i32,
) -> i32 {
    let host = &mut *(ctx as *mut Host);
    host.code_calls += 1;
    let hash: [u8; 32] = std::slice::from_raw_parts(code_hash, 32).try_into().unwrap();
    match host.code.get(&hash) {
        Some(code) if code.len() as i32 <= buf_len => {
            std::ptr::copy_nonoverlapping(code.as_ptr(), out_buf, code.len());
            code.len() as i32
        }
        Some(code) => code.len() as i32, // ask to be called again with a bigger buffer
        None => -1,
    }
}

unsafe extern "C" fn get_block_hash(_ctx: *mut c_void, _number: u64, out_hash: *mut u8) -> i32 {
    std::ptr::write_bytes(out_hash, 0, 32);
    0
}

fn u256(v: u128) -> [u8; 32] {
    let mut out = [0u8; 32];
    out[..16].copy_from_slice(&v.to_le_bytes());
    out
}

fn main() {
    let abi = unsafe { nm_evm_abi_version() };
    println!("library ABI {abi}, bindings expect {NM_EVM_ABI_VERSION}");
    assert_eq!(abi, NM_EVM_ABI_VERSION, "ABI mismatch");
    let provenance = nethermind_evm_sys::build_info();
    println!("{provenance}");
    assert!(
        !provenance.contains("commit=unknown"),
        "the bundle was built outside a git checkout, so it cannot be tied to a node revision"
    );

    let code_hash = keccak(&CONTRACT);
    let empty_code_hash = keccak(&[]);

    let mut host = Host {
        accounts: HashMap::from([
            (SENDER, (0u64, u256(10u128.pow(21)), empty_code_hash)),
            (CONTRACT_ADDR, (0u64, u256(0), code_hash)),
        ]),
        code: HashMap::from([(code_hash, CONTRACT.to_vec())]),
        account_calls: 0,
        storage_calls: 0,
        code_calls: 0,
    };

    let host_ffi = NmEvmHost {
        ctx: &mut host as *mut Host as *mut c_void,
        get_account: Some(get_account),
        get_storage: Some(get_storage),
        get_code: Some(get_code),
        get_block_hash: Some(get_block_hash),
    };

    let engine = unsafe { nm_evm_engine_new(&host_ffi, 1) };
    assert!(!engine.is_null(), "engine creation failed");

    let block = NmEvmBlock {
        number: 15_537_396,              // past the merge
        timestamp: 0x681b_3057,          // Prague
        gas_limit: 30_000_000,
        coinbase: [0x99; 20],
        base_fee: u256(0),
        prev_randao: [0u8; 32],
        excess_blob_gas: 0,
        has_excess_blob_gas: 1,
    };
    let rc = unsafe { nm_evm_set_block(engine, &block) };
    assert_eq!(rc, NM_OK, "set_block returned {rc}");

    // ---- 1. plain value transfer -------------------------------------------------
    let tx = NmEvmTx {
        tx_type: NM_TX_LEGACY,
        nonce: 0,
        gas_limit: 100_000,
        max_fee_per_gas: u256(1),
        value: u256(1),
        sender: SENDER,
        to: RECIPIENT,
        has_to: 1,
        ..Default::default()
    };
    let mut result = NmEvmResult::default();
    let rc = unsafe { nm_evm_execute(engine, &tx, NM_EXEC_DEFAULT, &mut result) };
    assert_eq!(rc, NM_OK, "execute returned {rc}: {}", last_error(engine));
    println!(
        "\ntransfer     success={} gas_used={} accounts={} storage={} logs={}",
        result.success, result.gas_used, result.account_count, result.storage_count, result.log_count
    );
    assert_eq!(result.success, 1);
    assert_eq!(result.gas_used, 21_000, "a plain transfer costs 21000");
    assert!(result.account_count >= 2, "sender and recipient must both change");
    unsafe { nm_evm_result_free(&mut result) };

    // ---- 2. call the contract ----------------------------------------------------
    let tx = NmEvmTx {
        tx_type: NM_TX_LEGACY,
        nonce: 1,
        gas_limit: 200_000,
        max_fee_per_gas: u256(1),
        value: u256(0),
        sender: SENDER,
        to: CONTRACT_ADDR,
        has_to: 1,
        ..Default::default()
    };
    let mut result = NmEvmResult::default();
    let rc = unsafe { nm_evm_execute(engine, &tx, NM_EXEC_DEFAULT, &mut result) };
    assert_eq!(rc, NM_OK, "execute returned {rc}: {}", last_error(engine));
    println!(
        "contract     success={} gas_used={} accounts={} storage={} logs={}",
        result.success, result.gas_used, result.account_count, result.storage_count, result.log_count
    );
    assert_eq!(result.success, 1, "contract call failed");

    let storage = unsafe { std::slice::from_raw_parts(result.storage, result.storage_count as usize) };
    assert_eq!(storage.len(), 1, "one SSTORE expected");
    assert_eq!(storage[0].address, CONTRACT_ADDR);
    assert_eq!(storage[0].key[0], 1);
    assert_eq!(storage[0].value[0], 42, "slot 1 should hold 42");
    println!("  storage    {}[1] = {}", hex20(&storage[0].address), storage[0].value[0]);

    let logs = unsafe { std::slice::from_raw_parts(result.logs, result.log_count as usize) };
    assert_eq!(logs.len(), 1, "one LOG1 expected");
    let topics = unsafe { std::slice::from_raw_parts(logs[0].topics, 32) };
    assert_eq!(topics[31], 0xAA, "topic should be 0xAA");
    println!("  log        address={} topic0=0x{:02x}", hex20(&logs[0].address), topics[31]);

    let accounts = unsafe { std::slice::from_raw_parts(result.accounts, result.account_count as usize) };
    for a in accounts {
        println!(
            "  account    {} exists={} nonce={} balance[0..4]={:?}",
            hex20(&a.address), a.exists, a.nonce, &a.balance[..4]
        );
    }
    unsafe { nm_evm_result_free(&mut result) };

    // ---- 3. the boundary actually got crossed ------------------------------------
    println!(
        "\nhost callbacks: account={} storage={} code={}",
        host.account_calls, host.storage_calls, host.code_calls
    );
    assert!(host.account_calls > 0, "the engine never asked the host for an account");
    assert!(host.code_calls > 0, "the engine never asked the host for the contract code");

    unsafe { nm_evm_engine_free(engine) };
    println!("\nOK");
}

fn hex20(bytes: &[u8; 20]) -> String {
    format!("0x{}{}", hex(&bytes[..2]), "..")
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn last_error(engine: *mut NmEvmEngine) -> String {
    let len = unsafe { nm_evm_last_error(engine, std::ptr::null_mut(), 0) };
    if len <= 0 {
        return String::new();
    }
    let mut buf = vec![0u8; len as usize + 1];
    unsafe { nm_evm_last_error(engine, buf.as_mut_ptr(), buf.len() as i32) };
    buf.pop();
    String::from_utf8_lossy(&buf).into_owned()
}
