//! Raw FFI bindings to Nethermind's EVM.
//!
//! This crate is the mechanical mirror of `include/nethermind_evm.h` and adds no policy. It links
//! against a prebuilt bundle located by `build.rs`; building it needs no .NET SDK and no
//! Nethermind checkout.
//!
//! Every 256-bit value is 32 bytes **little-endian**. Addresses and hashes are big-endian byte
//! strings, as on chain.
//!
//! # Safety
//!
//! An [`NmEvmEngine`] is not `Sync` and not `Send`: create one per thread. Pointers handed to the
//! engine are borrowed for the duration of the call. Pointers handed back live until
//! [`nm_evm_result_free`].

#![allow(non_camel_case_types)]

use core::ffi::c_void;

/// The ABI this binding was generated against. Compare with [`nm_evm_abi_version`] at startup.
pub const NM_EVM_ABI_VERSION: u32 = 1;

pub const NM_OK: i32 = 0;
pub const NM_ERR_ARGUMENT: i32 = -1;
pub const NM_ERR_ENGINE: i32 = -2;
pub const NM_ERR_NO_BLOCK: i32 = -3;
pub const NM_ERR_TX_REJECTED: i32 = -4;
pub const NM_ERR_INTERNAL: i32 = -5;

pub const NM_TX_LEGACY: u8 = 0;
pub const NM_TX_EIP2930: u8 = 1;
pub const NM_TX_EIP1559: u8 = 2;
pub const NM_TX_EIP4844: u8 = 3;
pub const NM_TX_EIP7702: u8 = 4;

pub const NM_EXEC_DEFAULT: u32 = 0;
pub const NM_EXEC_SKIP_VALIDATION: u32 = 1;

pub type nm_get_account_fn = unsafe extern "C" fn(
    ctx: *mut c_void,
    address: *const u8,
    out_nonce: *mut u64,
    out_balance: *mut u8,
    out_code_hash: *mut u8,
) -> i32;

pub type nm_get_storage_fn = unsafe extern "C" fn(
    ctx: *mut c_void,
    address: *const u8,
    key: *const u8,
    out_value: *mut u8,
) -> i32;

pub type nm_get_code_fn = unsafe extern "C" fn(
    ctx: *mut c_void,
    code_hash: *const u8,
    out_buf: *mut u8,
    buf_len: i32,
) -> i32;

pub type nm_get_block_hash_fn =
    unsafe extern "C" fn(ctx: *mut c_void, number: u64, out_hash: *mut u8) -> i32;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmEvmHost {
    pub ctx: *mut c_void,
    pub get_account: Option<nm_get_account_fn>,
    pub get_storage: Option<nm_get_storage_fn>,
    pub get_code: Option<nm_get_code_fn>,
    pub get_block_hash: Option<nm_get_block_hash_fn>,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct NmEvmBlock {
    pub number: u64,
    pub timestamp: u64,
    pub gas_limit: u64,
    pub coinbase: [u8; 20],
    pub base_fee: [u8; 32],
    pub prev_randao: [u8; 32],
    pub excess_blob_gas: u64,
    pub has_excess_blob_gas: i32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmEvmTx {
    pub tx_type: u8,
    pub nonce: u64,
    pub gas_limit: u64,
    pub max_fee_per_gas: [u8; 32],
    pub max_priority_fee_per_gas: [u8; 32],
    pub value: [u8; 32],
    pub sender: [u8; 20],
    pub to: [u8; 20],
    pub has_to: i32,
    pub data: *const u8,
    pub data_len: i32,
}

impl Default for NmEvmTx {
    fn default() -> Self {
        Self {
            tx_type: NM_TX_LEGACY,
            nonce: 0,
            gas_limit: 0,
            max_fee_per_gas: [0; 32],
            max_priority_fee_per_gas: [0; 32],
            value: [0; 32],
            sender: [0; 20],
            to: [0; 20],
            has_to: 0,
            data: core::ptr::null(),
            data_len: 0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmAccountChange {
    pub address: [u8; 20],
    pub exists: i32,
    pub nonce: u64,
    pub balance: [u8; 32],
    pub code_hash: [u8; 32],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmStorageChange {
    pub address: [u8; 20],
    pub key: [u8; 32],
    pub value: [u8; 32],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmCodeChange {
    pub code_hash: [u8; 32],
    pub code: *const u8,
    pub code_len: i32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmLog {
    pub address: [u8; 20],
    pub topic_count: i32,
    pub topics: *const u8,
    pub data: *const u8,
    pub data_len: i32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NmEvmResult {
    pub success: i32,
    pub gas_used: u64,
    pub gas_refunded: u64,
    pub output: *const u8,
    pub output_len: i32,
    pub accounts: *const NmAccountChange,
    pub account_count: i32,
    pub storage: *const NmStorageChange,
    pub storage_count: i32,
    pub code: *const NmCodeChange,
    pub code_count: i32,
    pub logs: *const NmLog,
    pub log_count: i32,
    pub _arena: *mut c_void,
}

impl Default for NmEvmResult {
    fn default() -> Self {
        // A zeroed result is what nm_evm_execute expects to be handed, and is safe to free.
        unsafe { core::mem::zeroed() }
    }
}

#[repr(C)]
pub struct NmEvmEngine {
    _opaque: [u8; 0],
}

extern "C" {
    pub fn nm_evm_abi_version() -> u32;
    pub fn nm_evm_engine_new(host: *const NmEvmHost, chain_id: u64) -> *mut NmEvmEngine;
    pub fn nm_evm_engine_free(engine: *mut NmEvmEngine);
    pub fn nm_evm_set_block(engine: *mut NmEvmEngine, block: *const NmEvmBlock) -> i32;
    pub fn nm_evm_execute(
        engine: *mut NmEvmEngine,
        tx: *const NmEvmTx,
        flags: u32,
        result: *mut NmEvmResult,
    ) -> i32;
    pub fn nm_evm_result_free(result: *mut NmEvmResult);
    pub fn nm_evm_last_error(engine: *mut NmEvmEngine, buf: *mut u8, buf_len: i32) -> i32;
    pub fn nm_evm_build_info(buf: *mut u8, buf_len: i32) -> i32;
}

/// The Nethermind revision this library was compiled from, as
/// `nethermind-evm <version> commit=<sha>[-dirty] abi=<n>`.
///
/// Compare the commit against the node the builder runs beside; a mismatch means the two can
/// disagree on execution, which shows up as an invalid block rather than an error.
pub fn build_info() -> String {
    let len = unsafe { nm_evm_build_info(core::ptr::null_mut(), 0) };
    if len <= 0 {
        return String::new();
    }
    let mut buf = vec![0u8; len as usize + 1];
    unsafe { nm_evm_build_info(buf.as_mut_ptr(), buf.len() as i32) };
    buf.pop();
    String::from_utf8_lossy(&buf).into_owned()
}

/// Struct layouts are the contract with the C header; a mismatch is silent corruption, so the
/// sizes are asserted rather than trusted.
#[cfg(test)]
mod layout {
    use super::*;

    #[test]
    fn sizes_match_the_header() {
        assert_eq!(core::mem::size_of::<NmAccountChange>(), 96);
        assert_eq!(core::mem::size_of::<NmStorageChange>(), 84);
        assert_eq!(core::mem::size_of::<NmCodeChange>(), 48);
        assert_eq!(core::mem::size_of::<NmLog>(), 48);
        assert_eq!(core::mem::size_of::<NmEvmResult>(), 112);
    }

    #[test]
    fn library_abi_matches_these_bindings() {
        assert_eq!(unsafe { nm_evm_abi_version() }, NM_EVM_ABI_VERSION);
    }
}
