// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Usage: nethermind-ffi-example <ffi_dir> <config> [Category.Name=value...]
//!
//! Starts the node, prints the head and tx pool events, then executes each hex-encoded block RLP read from stdin,
//! one per line. Stops the node on end of input. Mirrors ../example/main.c.

use std::ffi::{CString, c_char, c_int, c_void};
use std::io::{self, BufRead};
use std::process::ExitCode;
use std::{env, ptr, slice};

/// Declarations matching ../nethermind_ffi.h.
mod sys {
    use std::ffi::{c_char, c_int, c_void};

    pub const NM_OK: c_int = 0;

    #[repr(C)]
    pub struct NmNode {
        _private: [u8; 0],
    }

    #[repr(C)]
    #[derive(Default)]
    pub struct NmHead {
        pub number: u64,
        pub hash: [u8; 32],
        pub state_root: [u8; 32],
    }

    #[repr(C)]
    pub struct NmBlockResult {
        pub gas_used: u64,
        pub state_root: [u8; 32],
        pub receipts_root: [u8; 32],
        pub block_hash: [u8; 32],
        pub receipts_rlp: *mut u8,
        pub receipts_len: usize,
        pub error: *mut u8,
        pub error_len: usize,
    }

    pub type NmTxCallback = unsafe extern "C" fn(
        user_data: *mut c_void,
        event: i32,
        tx_hash: *const u8,
        tx: *const u8,
        tx_len: usize,
    );

    unsafe extern "C" {
        pub fn nm_start(
            ffi_dir: *const c_char,
            config: *const c_char,
            override_count: c_int,
            overrides: *const *const c_char,
            err: *mut c_char,
            err_len: usize,
        ) -> *mut NmNode;
        pub fn nm_wait_ready(node: *mut NmNode, timeout_ms: i32) -> c_int;
        pub fn nm_get_head(node: *mut NmNode, head: *mut NmHead) -> c_int;
        pub fn nm_execute_block(
            node: *mut NmNode,
            block_rlp: *const u8,
            block_rlp_len: usize,
            result: *mut NmBlockResult,
        ) -> c_int;
        pub fn nm_free_block_result(node: *mut NmNode, result: *mut NmBlockResult);
        pub fn nm_set_tx_callback(
            node: *mut NmNode,
            callback: Option<NmTxCallback>,
            user_data: *mut c_void,
        ) -> c_int;
        pub fn nm_stop(node: *mut NmNode, exit_code: i32) -> c_int;
        pub fn nm_join(node: *mut NmNode) -> c_int;
    }
}

/// Computed results of a block execution, copied out of the native buffers.
struct BlockExecution {
    status: c_int,
    gas_used: u64,
    block_hash: [u8; 32],
    state_root: [u8; 32],
    receipts_root: [u8; 32],
    receipts_rlp: Vec<u8>,
    error: Option<String>,
}

/// A running node; one per process.
struct Node(*mut sys::NmNode);

impl Node {
    fn start(ffi_dir: &str, config: &str, overrides: &[String]) -> Result<Node, String> {
        let ffi_dir = CString::new(ffi_dir).map_err(|e| e.to_string())?;
        let config = CString::new(config).map_err(|e| e.to_string())?;
        let overrides = overrides
            .iter()
            .map(|o| CString::new(o.as_str()))
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        let override_pointers: Vec<*const c_char> = overrides.iter().map(|o| o.as_ptr()).collect();

        let mut err = [0 as c_char; 1024];
        let node = unsafe {
            sys::nm_start(
                ffi_dir.as_ptr(),
                config.as_ptr(),
                override_pointers.len() as c_int,
                override_pointers.as_ptr(),
                err.as_mut_ptr(),
                err.len(),
            )
        };

        if node.is_null() {
            Err(unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }
                .to_string_lossy()
                .into_owned())
        } else {
            Ok(Node(node))
        }
    }

    fn wait_ready(&self, timeout_ms: i32) -> c_int {
        unsafe { sys::nm_wait_ready(self.0, timeout_ms) }
    }

    fn head(&self) -> Result<sys::NmHead, c_int> {
        let mut head = sys::NmHead::default();
        match unsafe { sys::nm_get_head(self.0, &mut head) } {
            sys::NM_OK => Ok(head),
            status => Err(status),
        }
    }

    fn execute_block(&self, block_rlp: &[u8]) -> BlockExecution {
        let mut result = sys::NmBlockResult {
            gas_used: 0,
            state_root: [0; 32],
            receipts_root: [0; 32],
            block_hash: [0; 32],
            receipts_rlp: ptr::null_mut(),
            receipts_len: 0,
            error: ptr::null_mut(),
            error_len: 0,
        };

        let status = unsafe {
            sys::nm_execute_block(self.0, block_rlp.as_ptr(), block_rlp.len(), &mut result)
        };
        let execution = BlockExecution {
            status,
            gas_used: result.gas_used,
            block_hash: result.block_hash,
            state_root: result.state_root,
            receipts_root: result.receipts_root,
            receipts_rlp: unsafe { copy(result.receipts_rlp, result.receipts_len) },
            error: (!result.error.is_null()).then(|| {
                String::from_utf8_lossy(unsafe {
                    slice::from_raw_parts(result.error, result.error_len)
                })
                .into_owned()
            }),
        };

        unsafe { sys::nm_free_block_result(self.0, &mut result) };
        execution
    }

    /// Sets (or with `None` clears) the tx pool event callback; it runs concurrently on the node's threads.
    fn set_tx_callback(&self, callback: Option<sys::NmTxCallback>) -> c_int {
        unsafe { sys::nm_set_tx_callback(self.0, callback, ptr::null_mut()) }
    }

    /// Stops the node and waits for it, returning its exit code.
    fn shutdown(self) -> c_int {
        unsafe {
            sys::nm_stop(self.0, 0);
            sys::nm_join(self.0)
        }
    }
}

unsafe extern "C" fn on_tx(
    _user_data: *mut c_void,
    event: i32,
    tx_hash: *const u8,
    _tx: *const u8,
    tx_len: usize,
) {
    let hash = unsafe { slice::from_raw_parts(tx_hash, 32) };
    println!("tx {event} {}  {tx_len} bytes", hex(hash));
}

unsafe fn copy(data: *const u8, length: usize) -> Vec<u8> {
    if data.is_null() {
        Vec::new()
    } else {
        unsafe { slice::from_raw_parts(data, length) }.to_vec()
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .fold(String::from("0x"), |s, b| s + &format!("{b:02x}"))
}

fn parse_hex(line: &str) -> Result<Vec<u8>, String> {
    let digits = line.trim().trim_start_matches("0x");
    (0..digits.len() / 2)
        .map(|i| u8::from_str_radix(&digits[2 * i..2 * i + 2], 16).map_err(|e| e.to_string()))
        .collect()
}

fn main() -> ExitCode {
    let args: Vec<String> = env::args().collect();
    if args.len() < 3 {
        eprintln!(
            "Usage: {} <ffi_dir> <config> [Category.Name=value...]",
            args[0]
        );
        return ExitCode::FAILURE;
    }

    let node = match Node::start(&args[1], &args[2], &args[3..]) {
        Ok(node) => node,
        Err(err) => {
            eprintln!("nm_start: {err}");
            return ExitCode::FAILURE;
        }
    };

    let status = node.wait_ready(600_000);
    println!("ready: {status}");
    if status == sys::NM_OK {
        match node.head() {
            Ok(head) => {
                println!("head number: {}", head.number);
                println!("head hash: {}", hex(&head.hash));
                println!("head state root: {}", hex(&head.state_root));
            }
            Err(status) => println!("head: {status}"),
        }

        println!("set tx callback: {}", node.set_tx_callback(Some(on_tx)));

        for line in io::stdin().lock().lines().map_while(Result::ok) {
            let block_rlp = match parse_hex(&line) {
                Ok(block_rlp) => block_rlp,
                Err(err) => {
                    println!("invalid hex: {err}");
                    continue;
                }
            };

            let execution = node.execute_block(&block_rlp);
            println!(
                "execute: {} gas used {}",
                execution.status, execution.gas_used
            );
            println!("  block hash: {}", hex(&execution.block_hash));
            println!("  state root: {}", hex(&execution.state_root));
            println!("  receipts root: {}", hex(&execution.receipts_root));
            println!("  receipts rlp: {} bytes", execution.receipts_rlp.len());
            if let Some(error) = execution.error {
                println!("  error: {error}");
            }
        }
    }

    node.set_tx_callback(None);
    let exit_code = node.shutdown();
    println!("exit code: {exit_code}");
    ExitCode::from(exit_code as u8)
}
