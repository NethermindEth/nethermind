//! What crosses the C boundary, and its byte layout. Integers are little-endian unless stated; the host decodes
//! the same layout in `ArkivNative.cs`.

use alloy_consensus::TxReceipt as _;
use alloy_primitives::{Address, B256, U256};
use reth_ethereum::evm::revm::context_interface::result::ExecutionResult;
use reth_ethereum::evm::revm::context_interface::result::HaltReason;
use serde::{Deserialize, Serialize};

/// One log, as a receipt carries it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Log {
    pub address: Address,
    pub topics: Vec<B256>,
    pub data: Vec<u8>,
}

/// A transaction's receipt, as reth's executor produced it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Receipt {
    pub success: bool,
    pub cumulative_gas_used: u64,
    pub logs: Vec<Log>,
}

impl From<&reth_ethereum::Receipt> for Receipt {
    fn from(receipt: &reth_ethereum::Receipt) -> Self {
        Self {
            success: receipt.status(),
            cumulative_gas_used: receipt.cumulative_gas_used(),
            logs: receipt
                .logs()
                .iter()
                .map(|log| Log { address: log.address, topics: log.topics().to_vec(), data: log.data.data.to_vec() })
                .collect(),
        }
    }
}

impl Receipt {
    /// `success:u8 cumulative_gas:u64 n_logs:u32 { address[20] n_topics:u32 topic[32]* data_len:u32 data }`
    pub fn encode_into(&self, out: &mut Vec<u8>) {
        out.push(u8::from(self.success));
        out.extend_from_slice(&self.cumulative_gas_used.to_le_bytes());
        out.extend_from_slice(&(self.logs.len() as u32).to_le_bytes());
        for log in &self.logs {
            out.extend_from_slice(log.address.as_slice());
            out.extend_from_slice(&(log.topics.len() as u32).to_le_bytes());
            for topic in &log.topics {
                out.extend_from_slice(topic.as_slice());
            }
            out.extend_from_slice(&(log.data.len() as u32).to_le_bytes());
            out.extend_from_slice(&log.data);
        }
    }
}

/// An account after the block, read from the committed store.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Account {
    pub address: Address,
    pub balance: U256,
    pub nonce: u64,
}

/// Everything the host needs from one executed block.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BlockResult {
    pub state_root: B256,
    pub gas_used: u64,
    pub receipts: Vec<Receipt>,
    pub requests_empty: bool,
    pub accounts: Vec<Account>,
    /// Each entity the block touched, with its `arkiv_getEntity`-shaped JSON after the block, or `None` once gone.
    pub entities: Vec<(B256, Option<Vec<u8>>)>,
}

/// ```text
/// state_root[32] gas_used:u64 requests_empty:u8
/// n_receipts:u32 { success:u8 cumulative_gas:u64 n_logs:u32 { address[20] n_topics:u32 topic[32]* data_len:u32 data } }
/// n_accounts:u32 { address[20] balance[32 BE] nonce:u64 }
/// n_entities:u32 { key[32] json_len:i32 (-1 = absent) json }
/// ```
impl BlockResult {
    pub fn encode(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(256);
        out.extend_from_slice(self.state_root.as_slice());
        out.extend_from_slice(&self.gas_used.to_le_bytes());
        out.push(u8::from(self.requests_empty));
        out.extend_from_slice(&(self.receipts.len() as u32).to_le_bytes());
        for receipt in &self.receipts {
            receipt.encode_into(&mut out);
        }
        out.extend_from_slice(&(self.accounts.len() as u32).to_le_bytes());
        for account in &self.accounts {
            out.extend_from_slice(account.address.as_slice());
            out.extend_from_slice(&account.balance.to_be_bytes::<32>());
            out.extend_from_slice(&account.nonce.to_le_bytes());
        }
        out.extend_from_slice(&(self.entities.len() as u32).to_le_bytes());
        for (key, json) in &self.entities {
            out.extend_from_slice(key.as_slice());
            match json {
                Some(json) => {
                    out.extend_from_slice(&(json.len() as i32).to_le_bytes());
                    out.extend_from_slice(json);
                }
                None => out.extend_from_slice(&(-1i32).to_le_bytes()),
            }
        }
        out
    }
}

/// The message for a state-root mismatch: the root execution reached, as hex, so it reads in a log as it is.
pub fn encode_mismatch(root: B256) -> String {
    format!("{root}")
}

/// A call to run without committing (`eth_call`, `eth_estimateGas`).
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CallRequest {
    pub from: Address,
    pub to: Option<Address>,
    #[serde(default, with = "alloy_primitives::hex::serde")]
    pub data: Vec<u8>,
    #[serde(default)]
    pub value: U256,
    pub gas: u64,
    #[serde(default)]
    pub gas_price: u128,
    #[serde(default)]
    pub nonce: u64,
    pub block_number: u64,
    pub timestamp: u64,
    pub base_fee: u64,
    pub block_gas_limit: u64,
    #[serde(default)]
    pub beneficiary: Address,
}

/// What a non-committing call came to.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CallResult {
    /// `success`, `revert` or `halt`.
    pub status: &'static str,
    pub gas_used: u64,
    #[serde(with = "alloy_primitives::hex::serde")]
    pub output: Vec<u8>,
    pub error: Option<String>,
}

impl CallResult {
    pub fn from_execution(result: ExecutionResult<HaltReason>) -> Self {
        match result {
            ExecutionResult::Success { gas, output, .. } => {
                Self { status: "success", gas_used: gas.total_gas_spent(), output: output.data().to_vec(), error: None }
            }
            ExecutionResult::Revert { gas, output, .. } => {
                Self { status: "revert", gas_used: gas.total_gas_spent(), output: output.to_vec(), error: Some("execution reverted".into()) }
            }
            ExecutionResult::Halt { reason, gas, .. } => {
                Self { status: "halt", gas_used: gas.total_gas_spent(), output: Vec::new(), error: Some(format!("{reason:?}")) }
            }
        }
    }
}
