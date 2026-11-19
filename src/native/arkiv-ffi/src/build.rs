//! Building a block, as `arkiv-reth`'s payload builder does (`arkiv-reth-payload-builder`): the protocol purge first,
//! then candidate transactions one at a time, each either taken or refused by Arkiv's executor, and the branch sealed
//! for its root without being committed — the block is committed when it comes back for import, like any other.
//!
//! The session lives on its own thread: reth's block executor borrows its state for the whole block, and the host
//! applies transactions across separate calls.

use std::sync::mpsc;
use std::thread;

use alloy_primitives::{Address, B256};
use arkiv_bindings::{MAX_PURGE_KEYS, PURGE_GAS_LIMIT};
use arkiv_interfaces::gas::purge_cost;
use arkiv_interfaces::query::{AnnotKey, AnnotVal, BuiltIn, Query};
use arkiv_reth_statemanager::ExecutionKey;
use reth_ethereum::evm::revm::State;
use reth_ethereum::primitives::{Recovered, SealedBlock};
use reth_ethereum::{Block, TransactionSigned};
use reth_evm::ConfigureEvm;
use reth_evm::execute::BlockExecutor;

use crate::wire;
use crate::{Node, Status};

/// Entities read per page while collecting expired candidates.
const PAGE: u64 = 100;

/// The most expired candidates read before choosing: the purge takes at most `MAX_PURGE_KEYS`, so a backlog this deep
/// is already far more than enough to choose from in expiry order.
const MAX_CANDIDATES: usize = 4096;

impl Node {
    /// The keys the purge of block `block` names: entities expired by then (`expiresAt ≤ block`), oldest expiry
    /// first and by key within one expiry, at most `MAX_PURGE_KEYS`, stopping before their purge cost would pass
    /// `PURGE_GAS_LIMIT` — the order and bounds of `arkiv-reth`'s chain pruning map. Read from GolemDB's own index at
    /// the head, which must be the block's parent.
    pub fn purge_keys(&self, block: u64) -> Result<Vec<B256>, (Status, String)> {
        let at = self.store_head();
        let expired = Query::Lte { key: AnnotKey::BuiltIn(BuiltIn::ExpiresAt), value: AnnotVal::U64(block) };
        let mut candidates = Vec::new();
        let mut offset = 0;
        loop {
            let page = arkiv_reth_rpc::store_reads::query(&*self.store, at, &expired, None, offset, PAGE, false)
                .map_err(|e| (Status::Internal, format!("expired entities: {e}")))?;
            let read = page.entities.len() as u64;
            candidates.extend(page.entities.into_iter().map(|e| (e.expires_at, B256::from(e.key), e.indexed_attribute_count())));
            offset += read;
            if read < PAGE || candidates.len() >= MAX_CANDIDATES {
                break;
            }
        }
        candidates.sort_by(|a, b| (a.0, a.1).cmp(&(b.0, b.1)));
        let mut keys = Vec::new();
        let mut gas = 0u64;
        for (_, key, indexed) in candidates.into_iter().take(MAX_PURGE_KEYS) {
            let next = gas.saturating_add(purge_cost(indexed));
            if next > PURGE_GAS_LIMIT {
                break;
            }
            gas = next;
            keys.push(key);
        }
        Ok(keys)
    }

    /// Open a build of the block `header` describes (its parent must be the store's head). The header's own roots are
    /// ignored; its number, time, beneficiary, base fee, gas limit, randomness and beacon root set the block's
    /// environment.
    pub fn build_begin(&self, header: alloy_consensus::Header) -> Result<BuildSession, (Status, String)> {
        let (_, head_tag, _) = self.head().map_err(crate::internal)?;
        if head_tag != header.parent_hash {
            return Err((Status::NotOnHead, format!("block {} builds on {}, the store's head is {head_tag}", header.number, header.parent_hash)));
        }
        let evm_config = self.evm_config.clone();
        let genesis_db = self.genesis_db.clone();
        let seals = self.seals.clone();
        let node = NodeRef { store: self.store.clone(), seals: seals.clone() };
        let (commands, inbox) = mpsc::channel::<Command>();
        let (ready_tx, ready_rx) = mpsc::channel::<Result<(), String>>();
        let worker = thread::Builder::new()
            .name("arkiv-build".into())
            .spawn(move || {
                let height = header.number;
                let block = SealedBlock::seal_slow(Block {
                    header,
                    body: reth_ethereum::BlockBody { transactions: vec![], ommers: vec![], withdrawals: Some(Default::default()) },
                });
                let mut state = State::builder().with_database(genesis_db).with_bundle_update().build();
                let mut executor = match evm_config.executor_for_block(&mut state, &block) {
                    Ok(executor) => executor,
                    Err(e) => return drop(ready_tx.send(Err(format!("{e}")))),
                };
                if let Err(e) = executor.apply_pre_execution_changes() {
                    return drop(ready_tx.send(Err(format!("{e}"))));
                }
                let _ = ready_tx.send(Ok(()));
                let mut taken: Vec<(Address, u64)> = Vec::new();
                while let Ok(command) = inbox.recv() {
                    match command {
                        Command::Apply(tx, sender, reply) => {
                            let nonce = alloy_consensus::Transaction::nonce(&tx);
                            let outcome = executor.execute_transaction(Recovered::new_unchecked(tx, sender));
                            let answer = match outcome {
                                Ok(_) => {
                                    taken.push((sender, nonce));
                                    Ok(wire::Receipt::from(executor.receipts().last().expect("a receipt per transaction")))
                                }
                                // Refused before it touched anything: the builder skips it, as reth's does.
                                Err(e) => Err(format!("{e}")),
                            };
                            let _ = reply.send(answer);
                        }
                        Command::Finish(reply) => {
                            let answer = match executor.finish() {
                                Ok((evm, result)) => {
                                    // Seals the block's branch; nothing is committed.
                                    let _ = reth_ethereum::evm::primitives::Evm::finish(evm);
                                    let key = ExecutionKey::new(height, taken.iter().map(|(s, n)| (s.into_array(), *n)).collect());
                                    match node.seals.root_of(&key) {
                                        Some(root) => Ok(wire::BlockResult {
                                            state_root: B256::from(root),
                                            gas_used: result.gas_used,
                                            receipts: result.receipts.iter().map(wire::Receipt::from).collect(),
                                            requests_empty: result.requests.is_empty(),
                                            accounts: Vec::new(),
                                            entities: Vec::new(),
                                        }),
                                        None => Err("the built block's Arkiv state was not sealed".to_owned()),
                                    }
                                }
                                Err(e) => Err(format!("{e}")),
                            };
                            node.discard_pending(height);
                            let _ = reply.send(answer);
                            return;
                        }
                    }
                }
                // Dropped without finishing: the executor and its unsealed branch go with it.
                node.discard_pending(height);
            })
            .map_err(|e| (Status::Internal, format!("spawn the build thread: {e}")))?;
        match ready_rx.recv() {
            Ok(Ok(())) => Ok(BuildSession { commands, worker: Some(worker) }),
            Ok(Err(e)) => Err((Status::InvalidBlock, e)),
            Err(_) => Err((Status::Internal, "the build thread ended before starting".into())),
        }
    }
}

/// What the build thread needs of the node.
struct NodeRef {
    store: arkiv_reth_statemanager::HostStore,
    seals: std::sync::Arc<arkiv_reth_statemanager::BlockSeals>,
}

impl NodeRef {
    fn discard_pending(&self, height: u64) {
        let _ = self.seals.adopt(&self.store, height, [0xff; 32], [0xff; 32], &[]);
    }
}

enum Command {
    Apply(TransactionSigned, Address, mpsc::Sender<Result<wire::Receipt, String>>),
    Finish(mpsc::Sender<Result<wire::BlockResult, String>>),
}

/// An open block build; see [`Node::build_begin`].
pub struct BuildSession {
    commands: mpsc::Sender<Command>,
    worker: Option<thread::JoinHandle<()>>,
}

impl BuildSession {
    /// Apply `tx` from `sender`: its receipt when Arkiv's executor takes it, the reason when it refuses it.
    pub fn apply(&self, tx: TransactionSigned, sender: Address) -> Result<wire::Receipt, String> {
        let (reply, answer) = mpsc::channel();
        self.commands.send(Command::Apply(tx, sender, reply)).map_err(|_| "the build has ended".to_owned())?;
        answer.recv().map_err(|_| "the build thread ended".to_owned())?
    }

    /// Finish the block: post-execution changes, then the seal. The result carries the root the header must carry.
    pub fn finish(mut self) -> Result<wire::BlockResult, String> {
        let (reply, answer) = mpsc::channel();
        self.commands.send(Command::Finish(reply)).map_err(|_| "the build has ended".to_owned())?;
        let result = answer.recv().map_err(|_| "the build thread ended".to_owned())?;
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        result
    }
}

impl Drop for BuildSession {
    fn drop(&mut self) {
        // Closing the channel ends the thread, which discards the unsealed branch.
        if let Some(worker) = self.worker.take() {
            let (closed, _) = mpsc::channel();
            self.commands = closed;
            let _ = worker.join();
        }
    }
}
