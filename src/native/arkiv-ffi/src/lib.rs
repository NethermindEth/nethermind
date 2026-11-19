//! C ABI over Arkiv's own block execution and GolemDB store, for the Nethermind Arkiv plugin.
//!
//! Nothing here re-implements Arkiv. A block is executed by reth's Ethereum block executor driven by
//! `arkiv_reth_executor::ArkivEvmFactory` — the exact executor the `arkiv-reth` node runs — over the same
//! GolemDB store, and committed through `BlockSeals::adopt`, which commits only a branch whose sealed root is
//! the root the block's header carries. The host (Nethermind) owns blocks, receipts, peers and the engine API;
//! this library owns Arkiv's state and its state root.
//!
//! # Memory
//!
//! Every buffer handed out is allocated here and must be returned through [`arkiv_free`]. Every function returns
//! an [`Status`]; on failure the output buffer carries a UTF-8 message instead of a result.

use std::path::Path;
use std::sync::Arc;

use alloy_consensus::{BlockHeader as _, TxReceipt as _};
use alloy_primitives::{Address, B256, Bytes, TxKind, U256};
use alloy_rlp::Decodable;
use alloy_sol_types::SolCall;
use arkiv_golemdb_state::accounts::{CELL_BALANCE, CELL_NONCE, record_key};
use arkiv_interfaces::gas::OpCost;
use arkiv_interfaces::store::{CommitId, ReadTarget, Store, StoreExt};
use arkiv_reth_chainspec::{ArkivChainSpec, ArkivChainSpecParser};
use arkiv_reth_executor::{ARKIV_ADDRESS, ArkivEvmFactory};
use arkiv_reth_statemanager::{BlockSeals, ExecutionKey, GenesisAccount, GolemAccounts, HostStore, seed_genesis};
use arkiv_rpc_types::entity::{Projection, entity_data_from};
use reth_cli::chainspec::ChainSpecParser;
use reth_ethereum::chainspec::EthChainSpec;
use reth_ethereum::evm::EthEvmConfig;
use reth_ethereum::evm::revm::State;
use reth_ethereum::evm::revm::db::states::bundle_state::BundleRetention;
use reth_ethereum::evm::revm::db::{CacheDB, EmptyDB};
use reth_ethereum::evm::revm::state::{AccountInfo, Bytecode};
use reth_ethereum::primitives::{Recovered, SealedBlock};
use reth_ethereum::{Block, TransactionSigned};
use reth_evm::ConfigureEvm;
use reth_evm::execute::BlockExecutor;

pub mod build;
pub mod store;
mod rpc;
pub mod wire;

#[cfg(test)]
mod tests;

/// Outcome of every exported call.
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Status {
    Ok = 0,
    /// The block executed, but to a state root other than the one its header carries. Nothing was committed;
    /// the buffer holds the root execution reached.
    StateRootMismatch = 1,
    /// The block is invalid: a transaction was refused or execution failed. Nothing was committed.
    InvalidBlock = 2,
    /// The block's parent is not the store's head, so it cannot run on it: GolemDB has no rewind.
    NotOnHead = 3,
    /// Bad input from the host.
    BadInput = 4,
    /// A store or internal failure.
    Internal = 5,
}

/// One opened chain: its spec, its store and the executor configuration over them.
pub struct Node {
    spec: Arc<ArkivChainSpec>,
    store: HostStore,
    seals: Arc<BlockSeals>,
    evm_config: EthEvmConfig<ArkivChainSpec, ArkivEvmFactory>,
    /// The genesis allocation as reth's database holds it. reth's own database is never written on an Arkiv node,
    /// so every block's system calls run over exactly this, and so do ours.
    genesis_db: CacheDB<EmptyDB>,
    genesis_hash: B256,
    genesis_root: B256,
}

impl Node {
    pub fn open(genesis_json: &str, directory: Option<&Path>) -> eyre::Result<Self> {
        let file_spec = ArkivChainSpecParser::parse(genesis_json)?;
        let config = golemdb_config(&file_spec)?;
        let accounts = || GenesisAccount::from_alloc(&file_spec.genesis().alloc);

        // Block 0 carries GolemDB's root over the genesis accounts, computed in memory exactly as arkiv-reth does
        // before any store opens, so the genesis hash is known before the store is tagged with it.
        let genesis_root = {
            let memory: HostStore = Arc::new(arkiv_golemdb::GolemDbStore::open_memory(&config)?);
            let commit = seed_genesis(&memory, [0; 32], accounts())
                .map_err(|e| eyre::eyre!("seed the in-memory genesis: {e:?}"))?
                .ok_or_else(|| eyre::eyre!("the in-memory store already had a genesis"))?;
            B256::from(memory.commit_hash(commit).map_err(|e| eyre::eyre!("genesis root: {e:?}"))?)
        };
        let spec = Arc::new(Arc::unwrap_or_clone(file_spec.clone()).with_genesis_state_root(genesis_root));
        let genesis_hash = spec.genesis_hash();

        let store: HostStore = match directory {
            // Archive history cannot come from GolemDB v1.0.0, which reads at its head only; the host keeps it.
            Some(directory) => Arc::new(
                arkiv_golemdb::GolemDbStore::open(directory, &config, Some(arkiv_golemdb::PruningConfig::new(0)))
                    .map_err(|e| eyre::eyre!("open GolemDB at {}: {e}", directory.display()))?,
            ),
            None => Arc::new(arkiv_golemdb::GolemDbStore::open_memory(&config)?),
        };
        if store.head() == CommitId::GENESIS {
            seed_genesis(&store, genesis_hash.0, accounts()).map_err(|e| eyre::eyre!("seed genesis: {e:?}"))?;
        }
        let tagged = store
            .commit_by_tag(genesis_hash.0)
            .map_err(|e| eyre::eyre!("read the store's genesis: {e:?}"))?
            .ok_or_else(|| eyre::eyre!("the Arkiv store holds another chain (no commit is tagged {genesis_hash})"))?;
        let stored_root = store.commit_hash(tagged).map_err(|e| eyre::eyre!("genesis root: {e:?}"))?;
        eyre::ensure!(stored_root == genesis_root.0, "the store's genesis root is not this chain's");

        let seals = Arc::new(BlockSeals::new());
        let evm_config =
            EthEvmConfig::new_with_evm_factory(spec.clone(), ArkivEvmFactory::new(store.clone(), seals.clone(), OpCost::FromStore));
        let genesis_db = genesis_database(&spec);
        Ok(Self { spec, store, seals, evm_config, genesis_db, genesis_hash, genesis_root })
    }

    /// The store's head: its commit number, the block hash it is tagged with and its root.
    pub fn head(&self) -> eyre::Result<(u64, B256, B256)> {
        let head = self.store.head();
        let tag = self.store.tag_of(head).map_err(|e| eyre::eyre!("tag_of: {e:?}"))?.unwrap_or_default();
        let root = self.store.commit_hash(head).map_err(|e| eyre::eyre!("commit_hash: {e:?}"))?;
        Ok((head.0, B256::from(tag), B256::from(root)))
    }

    /// Execute `block` on the store's head and, when `commit` is set and the root it reaches is the header's,
    /// commit it tagged with the block hash.
    pub fn execute_block(&self, block: Block, senders: Vec<Address>, commit: bool) -> Result<wire::BlockResult, (Status, String)> {
        let header = &block.header;
        let (_, head_tag, _) = self.head().map_err(internal)?;
        if head_tag != header.parent_hash {
            return Err((
                Status::NotOnHead,
                format!("block {} builds on {}, the store's head is {head_tag}", header.number, header.parent_hash),
            ));
        }
        if senders.len() != block.body.transactions.len() {
            return Err((Status::BadInput, "one sender per transaction".into()));
        }
        let sealed = SealedBlock::seal_slow(block);
        let height = sealed.header().number();
        let expected_root = sealed.header().state_root();
        let recovered: Vec<Recovered<TransactionSigned>> =
            sealed.body().transactions.iter().zip(&senders).map(|(tx, sender)| Recovered::new_unchecked(tx.clone(), *sender)).collect();

        let mut state = State::builder().with_database(self.genesis_db.clone()).with_bundle_update().build();
        let execution = {
            let mut executor = self.evm_config.executor_for_block(&mut state, &sealed).map_err(|e| (Status::InvalidBlock, format!("{e}")))?;
            let run = (|| {
                executor.apply_pre_execution_changes()?;
                for tx in &recovered {
                    executor.execute_transaction(tx.clone())?;
                }
                executor.finish()
            })();
            match run {
                Ok((evm, result)) => {
                    // `finish` is what seals the block's Arkiv branch, as reth's engine does after execution.
                    let _ = reth_ethereum::evm::primitives::Evm::finish(evm);
                    result
                }
                Err(e) => return Err((Status::InvalidBlock, format!("{e}"))),
            }
        };

        let key = ExecutionKey::new(height, recovered.iter().map(|tx| (tx.signer().into_array(), alloy_consensus::Transaction::nonce(tx.inner()))).collect());
        let root = self.seals.root_of(&key).map(B256::from).ok_or_else(|| {
            (Status::Internal, format!("block {height} executed but its Arkiv state was not sealed"))
        })?;
        let mut result = wire::BlockResult {
            state_root: root,
            gas_used: execution.gas_used,
            receipts: execution.receipts.iter().map(wire::Receipt::from).collect(),
            requests_empty: execution.requests.is_empty(),
            accounts: Vec::new(),
            entities: Vec::new(),
        };
        if root != expected_root {
            self.discard_pending(height);
            return Err((Status::StateRootMismatch, wire::encode_mismatch(root)));
        }
        if !commit {
            self.discard_pending(height);
            return Ok(result);
        }

        let committed = self
            .seals
            .adopt(&self.store, height, sealed.hash().0, root.0, &[])
            .map_err(|e| (Status::Internal, format!("commit block {height}: {e:?}")))?;
        self.seals.finish_adoption(sealed.hash().0);
        let commit = committed.ok_or_else(|| (Status::Internal, format!("no sealed candidate for block {height}")))?;

        // Every account the block wrote to: the executor mirrors each GolemDB balance and nonce write into reth's
        // account diff, so the bundle names them. The values are read back from the committed store, which is the
        // only authority on them.
        let accounts = GolemAccounts::new(self.store.clone(), commit);
        // Executed transactions sit in the state's transitions until merged; without this the bundle is empty
        // and no account would be reported (reth merges the same way after executing a block).
        state.merge_transitions(BundleRetention::PlainState);
        let bundle = state.take_bundle();
        let mut touched: Vec<Address> = bundle.state.keys().copied().collect();
        touched.sort();
        for address in touched {
            let account = accounts.account(address).map_err(|e| (Status::Internal, format!("read {address}: {e}")))?;
            if let Some(account) = account {
                result.accounts.push(wire::Account { address, balance: account.balance, nonce: account.nonce });
            }
        }
        for entity_key in touched_entities(&sealed, &execution.receipts) {
            let snapshot = arkiv_reth_rpc::store_reads::entity(&*self.store, commit, entity_key.0)
                .map_err(|e| (Status::Internal, format!("read entity {entity_key}: {e}")))?
                .map(|entity| serde_json::to_vec(&entity_data_from(entity, &Projection::all())).expect("entity data serialises"));
            result.entities.push((entity_key, snapshot));
        }
        Ok(result)
    }

    /// The store's head commit.
    pub(crate) fn store_head(&self) -> CommitId {
        self.store.head()
    }

    /// Drop the sealed candidates at `height` and below that will never be adopted.
    fn discard_pending(&self, height: u64) {
        // `adopt` against a root no candidate has drops every candidate at or below `height` and commits nothing.
        let _ = self.seals.adopt(&self.store, height, [0xff; 32], [0xff; 32], &[]);
    }

    /// An account at the head, as `eth_getBalance` / `eth_getTransactionCount` answer it.
    pub fn account(&self, address: Address) -> eyre::Result<Option<(U256, u64)>> {
        let record = self
            .store
            .get(ReadTarget::Commit(self.store.head()), record_key(address.into_array()), None, None)
            .map_err(|e| eyre::eyre!("{e:?}"))?
            .into_value();
        let Some(record) = record else { return Ok(None) };
        if !record.cells.iter().any(|(name, _)| name == CELL_BALANCE || name == CELL_NONCE) {
            return Ok(None);
        }
        let account = GolemAccounts::new(self.store.clone(), self.store.head())
            .account(address)
            .map_err(|e| eyre::eyre!("{e}"))?;
        Ok(account.map(|a| (a.balance, a.nonce)))
    }

    /// A call that commits nothing (`eth_call`, `eth_estimateGas`), executed by the Arkiv executor at the head.
    pub fn call(&self, request: &wire::CallRequest) -> Result<wire::CallResult, (Status, String)> {
        use reth_ethereum::evm::primitives::Evm as _;
        use reth_ethereum::evm::revm::context::TxEnv;

        let mut header = self.spec.genesis_header().clone();
        header.number = request.block_number;
        header.timestamp = request.timestamp;
        header.base_fee_per_gas = Some(request.base_fee);
        header.gas_limit = request.block_gas_limit;
        header.beneficiary = request.beneficiary;
        let mut env = self.evm_config.evm_env(&header).map_err(|e| (Status::Internal, format!("{e}")))?;
        // What reth sets for eth_call and eth_estimateGas: no base-fee check and no fee charging.
        env.cfg_env.disable_base_fee = true;
        env.cfg_env.disable_fee_charge = true;
        env.cfg_env.disable_nonce_check = true;
        let mut evm = self.evm_config.evm_with_env(State::builder().with_database(self.genesis_db.clone()).build(), env);
        let tx = TxEnv::builder()
            .caller(request.from)
            .kind(request.to.map_or(TxKind::Create, TxKind::Call))
            .data(Bytes::from(request.data.clone()))
            .value(request.value)
            .gas_limit(request.gas)
            .gas_price(request.gas_price)
            .chain_id(Some(self.spec.chain().id()))
            .nonce(request.nonce)
            .build()
            .map_err(|e| (Status::BadInput, format!("{e:?}")))?;
        let outcome = evm.transact_raw(tx);
        match outcome {
            Ok(result) => Ok(wire::CallResult::from_execution(result.result)),
            Err(e) => Err((Status::InvalidBlock, format!("{e}"))),
        }
    }
}

pub(crate) fn internal(e: eyre::Report) -> (Status, String) {
    (Status::Internal, format!("{e:#}"))
}

/// The entity keys a block touched: the subject of every entity event the Arkiv address logged, and every key a
/// protocol purge named (a purge logs nothing).
fn touched_entities(block: &SealedBlock<Block>, receipts: &[reth_ethereum::Receipt]) -> Vec<B256> {
    let mut keys = Vec::new();
    for receipt in receipts {
        for log in receipt.logs() {
            if log.address == ARKIV_ADDRESS
                && let Some(key) = log.topics().get(1)
            {
                keys.push(*key);
            }
        }
    }
    for tx in &block.body().transactions {
        use alloy_consensus::Transaction as _;
        if tx.to() == Some(ARKIV_ADDRESS)
            && tx.input().starts_with(&arkiv_bindings::protocol::purgeExpiredCall::SELECTOR)
            && let Ok(call) = arkiv_bindings::protocol::purgeExpiredCall::abi_decode_raw(&tx.input()[4..])
        {
            keys.extend(call.entityKeys);
        }
    }
    keys.sort();
    keys.dedup();
    keys
}

/// reth's database on an Arkiv node: the genesis allocation, which it never writes after.
fn genesis_database(spec: &ArkivChainSpec) -> CacheDB<EmptyDB> {
    let mut db = CacheDB::new(EmptyDB::default());
    for (address, account) in &spec.genesis().alloc {
        let code = account.code.clone().map(Bytecode::new_raw);
        let info = AccountInfo {
            balance: account.balance,
            nonce: account.nonce.unwrap_or_default(),
            code_hash: code.as_ref().map_or(reth_ethereum::evm::revm::primitives::KECCAK_EMPTY, |c| c.hash_slow()),
            code,
            ..Default::default()
        };
        db.insert_account_info(*address, info);
        if let Some(storage) = &account.storage {
            for (slot, value) in storage {
                let _ = db.insert_account_storage(*address, U256::from_be_bytes(slot.0), U256::from_be_bytes(value.0));
            }
        }
    }
    db
}

/// GolemDB's settings for the chain, from its genesis file, with Arkiv's segments: `arkiv-reth`'s
/// `golemdb_config`, which is private to its binary. They are part of the state root, so they must be the same.
fn golemdb_config(spec: &ArkivChainSpec) -> eyre::Result<arkiv_golemdb::GolemDbConfig> {
    use arkiv_interfaces::constants::{MAX_ATTRIBUTE_NAME_BYTES, MAX_BYTES_VALUE_LEN, MAX_STR_BYTES};
    use arkiv_interfaces::entity_records::{MAX_RECORD_CELLS, MAX_RECORD_INDEXED_CELLS};
    let limit = |bytes: usize| u32::try_from(bytes).expect("Arkiv's limits fit in u32");
    let limits = arkiv_golemdb::CellLimits {
        max_cell_name_len: limit(MAX_ATTRIBUTE_NAME_BYTES),
        max_str_len: limit(MAX_STR_BYTES),
        max_bytes_len: limit(MAX_BYTES_VALUE_LEN),
    };
    let count = |cells: usize| u64::try_from(cells).expect("Arkiv's limits fit in u64");
    let record_limits =
        arkiv_golemdb::RecordLimits { max_cells: count(MAX_RECORD_CELLS), max_indexed_cells: count(MAX_RECORD_INDEXED_CELLS) };
    let genesis = spec.golemdb().ok_or_else(|| eyre::eyre!("the genesis file has no config.arkiv.golemdb"))?;
    eyre::ensure!(genesis.cell_limits == limits, "config.arkiv.golemdb.cell_limits must be Arkiv's limits, {limits:?}");
    eyre::ensure!(
        genesis.record_limits == record_limits,
        "config.arkiv.golemdb.record_limits must be Arkiv's limits, {record_limits:?}"
    );
    Ok(arkiv_golemdb::GolemDbConfig {
        genesis: genesis.clone(),
        host_schemas: vec![arkiv_reth_chainspec::host_metering_schema()],
        segments: arkiv_reth_statemanager::segments::ALL
            .iter()
            .map(|&(name, columns)| arkiv_golemdb::SegmentSpec { name: name.to_owned(), columns })
            .collect(),
    })
}

// ---------------------------------------------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------------------------------------------

/// Hand `bytes` to the host. Freed with [`arkiv_free`].
fn give(bytes: Vec<u8>, out_ptr: *mut *mut u8, out_len: *mut usize) {
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    let ptr = Box::into_raw(boxed) as *mut u8;
    // SAFETY: the host passes valid out pointers on every call.
    unsafe {
        *out_ptr = ptr;
        *out_len = len;
    }
}

/// # Safety
/// `ptr`/`len` must name `len` readable bytes, or `len` must be zero.
unsafe fn slice<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if len == 0 { &[] } else { unsafe { std::slice::from_raw_parts(ptr, len) } }
}

fn guarded(out_ptr: *mut *mut u8, out_len: *mut usize, f: impl FnOnce() -> Result<Vec<u8>, (Status, String)>) -> i32 {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(Ok(bytes)) => {
            give(bytes, out_ptr, out_len);
            Status::Ok as i32
        }
        Ok(Err((status, message))) => {
            give(message.into_bytes(), out_ptr, out_len);
            status as i32
        }
        Err(panic) => {
            let message = panic
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| panic.downcast_ref::<&str>().map(|s| (*s).to_owned()))
                .unwrap_or_else(|| "panic".to_owned());
            give(format!("panic: {message}").into_bytes(), out_ptr, out_len);
            Status::Internal as i32
        }
    }
}

/// Open a chain from its genesis file (with `config.arkiv.golemdb`) over the GolemDB store in `dir` — created
/// and seeded with genesis when empty. An empty `dir` opens an in-memory store. On success `out_ptr` holds
/// `handle (8 bytes LE) ‖ genesis root (32) ‖ genesis hash (32)`.
///
/// # Safety
/// Pointers must be valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_open(
    genesis: *const u8,
    genesis_len: usize,
    dir: *const u8,
    dir_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    let genesis = unsafe { slice(genesis, genesis_len) };
    let dir = unsafe { slice(dir, dir_len) };
    guarded(out_ptr, out_len, || {
        let genesis = std::str::from_utf8(genesis).map_err(|e| (Status::BadInput, format!("genesis: {e}")))?;
        let dir = std::str::from_utf8(dir).map_err(|e| (Status::BadInput, format!("dir: {e}")))?;
        let node = Node::open(genesis, (!dir.is_empty()).then(|| Path::new(dir))).map_err(internal)?;
        let mut out = Vec::with_capacity(72);
        out.extend_from_slice(&0u64.to_le_bytes());
        out.extend_from_slice(node.genesis_root.as_slice());
        out.extend_from_slice(node.genesis_hash.as_slice());
        let handle = Box::into_raw(Box::new(node)) as u64;
        out[..8].copy_from_slice(&handle.to_le_bytes());
        Ok(out)
    })
}

/// # Safety
/// `handle` must come from [`arkiv_open`] and not be used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_close(handle: u64) {
    if handle != 0 {
        drop(unsafe { Box::from_raw(handle as *mut Node) });
    }
}

/// # Safety
/// `ptr`/`len` must be a buffer this library handed out.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)) });
    }
}

/// The store's head: `commit (8 LE) ‖ block hash tag (32) ‖ root (32)`.
///
/// # Safety
/// `handle` must be live; out pointers valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_head(handle: u64, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    guarded(out_ptr, out_len, || {
        let (commit, tag, root) = node.head().map_err(internal)?;
        let mut out = commit.to_le_bytes().to_vec();
        out.extend_from_slice(tag.as_slice());
        out.extend_from_slice(root.as_slice());
        Ok(out)
    })
}

/// Execute an RLP block whose transactions were sent by `senders` (20 bytes each), and commit it when `commit` is
/// non-zero and its state root matches. The output is [`wire::BlockResult`]'s encoding, or for
/// [`Status::StateRootMismatch`] the root reached.
///
/// # Safety
/// `handle` must be live; pointers valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_execute_block(
    handle: u64,
    block_rlp: *const u8,
    block_len: usize,
    senders: *const u8,
    senders_len: usize,
    commit: i32,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    let block_rlp = unsafe { slice(block_rlp, block_len) };
    let senders = unsafe { slice(senders, senders_len) };
    guarded(out_ptr, out_len, || {
        let block = Block::decode(&mut &block_rlp[..]).map_err(|e| (Status::BadInput, format!("block rlp: {e}")))?;
        if senders.len() % 20 != 0 {
            return Err((Status::BadInput, "senders are 20 bytes each".into()));
        }
        let senders = senders.chunks_exact(20).map(Address::from_slice).collect();
        node.execute_block(block, senders, commit != 0).map(|result| result.encode())
    })
}

/// An account at the head: `exists (1) ‖ balance (32 BE) ‖ nonce (8 LE)`.
///
/// # Safety
/// `handle` must be live; `address` 20 readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_account(handle: u64, address: *const u8, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    let address = Address::from_slice(unsafe { slice(address, 20) });
    guarded(out_ptr, out_len, || {
        let account = node.account(address).map_err(internal)?;
        let mut out = vec![u8::from(account.is_some())];
        let (balance, nonce) = account.unwrap_or_default();
        out.extend_from_slice(&balance.to_be_bytes::<32>());
        out.extend_from_slice(&nonce.to_le_bytes());
        Ok(out)
    })
}

/// A non-committing call at the head; request and response are JSON (see [`wire::CallRequest`]).
///
/// # Safety
/// `handle` must be live; pointers valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_call(handle: u64, request: *const u8, request_len: usize, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    let request = unsafe { slice(request, request_len) };
    guarded(out_ptr, out_len, || {
        let request: wire::CallRequest = serde_json::from_slice(request).map_err(|e| (Status::BadInput, format!("{e}")))?;
        node.call(&request).map(|r| serde_json::to_vec(&r).expect("call result serialises"))
    })
}

/// An `arkiv_*` JSON-RPC method at the head. `params` is the JSON params array, `head_number` the block number of
/// the store's head. The output is a JSON object with either `result` or `error`.
///
/// # Safety
/// `handle` must be live; pointers valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_rpc(
    handle: u64,
    method: *const u8,
    method_len: usize,
    params: *const u8,
    params_len: usize,
    head_number: u64,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    let method = unsafe { slice(method, method_len) };
    let params = unsafe { slice(params, params_len) };
    guarded(out_ptr, out_len, || {
        let method = std::str::from_utf8(method).map_err(|e| (Status::BadInput, format!("{e}")))?;
        let params: serde_json::Value =
            if params.is_empty() { serde_json::Value::Array(vec![]) } else { serde_json::from_slice(params).map_err(|e| (Status::BadInput, format!("{e}")))? };
        Ok(serde_json::to_vec(&rpc::dispatch(&node.store, method, &params, head_number)).expect("json"))
    })
}

/// The purge keys for block `block` (see [`Node::purge_keys`]): 32 bytes each.
///
/// # Safety
/// `handle` must be live; out pointers valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_purge_keys(handle: u64, block: u64, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    guarded(out_ptr, out_len, || Ok(node.purge_keys(block)?.into_iter().flat_map(|k| k.0).collect()))
}

/// Open a block build over the RLP header of the block to build (see [`Node::build_begin`]). Output: the session
/// handle (8 bytes LE), for [`arkiv_build_apply`], then [`arkiv_build_finish`] or [`arkiv_build_abort`].
///
/// # Safety
/// `handle` must be live; pointers valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_build_begin(handle: u64, header_rlp: *const u8, header_len: usize, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let node = unsafe { &*(handle as *const Node) };
    let header_rlp = unsafe { slice(header_rlp, header_len) };
    guarded(out_ptr, out_len, || {
        let header = alloy_consensus::Header::decode(&mut &header_rlp[..]).map_err(|e| (Status::BadInput, format!("header rlp: {e}")))?;
        let session = Box::into_raw(Box::new(node.build_begin(header)?)) as u64;
        Ok(session.to_le_bytes().to_vec())
    })
}

/// Apply one EIP-2718 transaction from `sender` (20 bytes) to an open build. Output: its receipt
/// ([`wire::Receipt::encode_into`]), or with [`Status::InvalidBlock`] the reason Arkiv's executor refused it — the
/// build goes on without it.
///
/// # Safety
/// `session` must be live; pointers valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_build_apply(session: u64, tx: *const u8, tx_len: usize, sender: *const u8, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let session = unsafe { &*(session as *const build::BuildSession) };
    let tx = unsafe { slice(tx, tx_len) };
    let sender = Address::from_slice(unsafe { slice(sender, 20) });
    guarded(out_ptr, out_len, || {
        use alloy_eips::eip2718::Decodable2718;
        let tx = TransactionSigned::decode_2718(&mut &tx[..]).map_err(|e| (Status::BadInput, format!("transaction: {e}")))?;
        let receipt = session.apply(tx, sender).map_err(|e| (Status::InvalidBlock, e))?;
        let mut out = Vec::new();
        receipt.encode_into(&mut out);
        Ok(out)
    })
}

/// Finish and free a build. Output: [`wire::BlockResult`]'s encoding, whose root the block's header must carry.
///
/// # Safety
/// `session` must come from [`arkiv_build_begin`] and not be used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_build_finish(session: u64, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let session = unsafe { Box::from_raw(session as *mut build::BuildSession) };
    guarded(out_ptr, out_len, move || session.finish().map(|r| r.encode()).map_err(|e| (Status::InvalidBlock, e)))
}

/// Abandon and free a build; nothing it applied is kept.
///
/// # Safety
/// `session` must come from [`arkiv_build_begin`] and not be used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_build_abort(session: u64) {
    if session != 0 {
        drop(unsafe { Box::from_raw(session as *mut build::BuildSession) });
    }
}

/// Every account of the GolemDB store in `dir`, which this process must not have open (see
/// [`store::account_addresses`]): 20 bytes each.
///
/// # Safety
/// Pointers must be valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn arkiv_store_accounts(dir: *const u8, dir_len: usize, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let dir = unsafe { slice(dir, dir_len) };
    guarded(out_ptr, out_len, || {
        let dir = std::str::from_utf8(dir).map_err(|e| (Status::BadInput, format!("dir: {e}")))?;
        let addresses = store::account_addresses(Path::new(dir)).map_err(internal)?;
        Ok(addresses.into_iter().flat_map(|a| a.into_array()).collect())
    })
}
