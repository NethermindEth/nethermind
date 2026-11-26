//! Building a block, as `arkiv-reth`'s payload builder does (`arkiv-reth-payload-builder`): the protocol purge first,
//! then candidate transactions one at a time, each either taken or refused by Arkiv's executor, and the branch sealed
//! for its root without being committed — the block is committed when it comes back for import, like any other.
//!
//! The session lives on its own thread: reth's block executor borrows its state for the whole block, and the host
//! applies transactions across separate calls.

use std::collections::BinaryHeap;
use std::sync::{Arc, mpsc};
use std::thread;

use alloy_primitives::{Address, B256};
use arkiv_bindings::{MAX_PURGE_KEYS, PURGE_GAS_LIMIT};
use arkiv_interfaces::gas::{OpCost, purge_cost};
use arkiv_interfaces::entity::AttributeType;
use arkiv_interfaces::query::{AnnotKey, AnnotVal, BuiltIn, Direction, OrderBy, Query};
use arkiv_interfaces::store::Sort;
use arkiv_reth_executor::ArkivEvmFactory;
use arkiv_reth_statemanager::{BlockSeals, ExecutionKey};
use reth_ethereum::evm::EthEvmConfig;
use reth_ethereum::evm::revm::State;
use reth_ethereum::primitives::{Recovered, SealedBlock};
use reth_ethereum::{Block, TransactionSigned};
use reth_evm::ConfigureEvm;
use reth_evm::execute::BlockExecutor;

use crate::wire;
use crate::{Node, Status};

/// Entities read per page while collecting expired candidates.
const PAGE: u64 = 100;

impl Node {
    /// The keys the purge of block `block` names: entities expired by then (`expiresAt ≤ block`), oldest expiry
    /// first and by key within one expiry, at most `MAX_PURGE_KEYS`, stopping before their purge cost would pass
    /// `PURGE_GAS_LIMIT` — the order and bounds of `arkiv-reth`'s chain pruning map. Read from GolemDB's own index at
    /// the head, which must be the block's parent.
    pub fn purge_keys(&self, block: u64) -> Result<Vec<B256>, (Status, String)> {
        let at = self.store_head();
        let expired = Query::Lte { key: AnnotKey::BuiltIn(BuiltIn::ExpiresAt), value: AnnotVal::U64(block) };
        // Read in ascending expiry, as arkiv-reth's index (`ORDER BY expires_at, entity_id`) is. The store breaks
        // expiry ties in creation order, not by key, so once `MAX_PURGE_KEYS` are in hand the rest of the last
        // expiry is read too, only the first keys are kept in (expiry, key) order. Memory is bounded by MAX_PURGE_KEYS;
        // work still includes every entity tied at the final expiry, which can be large.
        let order = Sort::from(&OrderBy { key: AnnotKey::BuiltIn(BuiltIn::ExpiresAt), attr_type: AttributeType::U64, direction: Direction::Ascending });
        let mut candidates: BinaryHeap<(u64, B256, usize)> = BinaryHeap::new();
        let mut offset = 0;
        'pages: loop {
            let page = arkiv_reth_rpc::store_reads::query(&*self.store, at, &expired, Some(&order), offset, PAGE, false)
                .map_err(|e| (Status::Internal, format!("expired entities: {e}")))?;
            let read = page.entities.len() as u64;
            for entity in page.entities {
                if candidates.len() >= MAX_PURGE_KEYS && entity.expires_at > candidates.peek().expect("nonempty candidates").0 {
                    break 'pages;
                }
                retain_purge_candidate(&mut candidates, (entity.expires_at, B256::from(entity.key), entity.indexed_attribute_count()));
            }
            offset += read;
            if read < PAGE {
                break;
            }
        }
        let candidates = candidates.into_sorted_vec();
        let mut keys = Vec::new();
        let mut gas = 0u64;
        for (_, key, indexed) in candidates {
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
        let (_, head_tag, head_root) = self.head().map_err(crate::internal)?;
        if head_tag != header.parent_hash {
            return Err((Status::NotOnHead, format!("block {} builds on {}, the store's head is {head_tag}", header.number, header.parent_hash)));
        }
        // The session seals into a registry of its own: discarding its branch must not touch another build's at the
        // same height (the node's registry drops every candidate at a height when one is adopted or discarded).
        let seals = Arc::new(BlockSeals::new());
        let evm_config =
            EthEvmConfig::new_with_evm_factory(self.spec.clone(), ArkivEvmFactory::new(self.store.clone(), seals.clone(), OpCost::FromStore));
        let genesis_db = self.genesis_db.clone();
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
                                    // Nothing charged leaves no seal and the parent's state as it was.
                                    match seals.root_of(&key).map(B256::from).or(taken.is_empty().then_some(head_root)) {
                                        Some(root) => Ok(wire::BlockResult {
                                            state_root: root,
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
                            // The session's registry, and the sealed branch in it, is dropped as the thread ends:
                            // nothing is committed.
                            let _ = reply.send(answer);
                            return;
                        }
                    }
                }
                // Dropped without finishing: the executor, its branch and the session's registry go with it.
            })
            .map_err(|e| (Status::Internal, format!("spawn the build thread: {e}")))?;
        match ready_rx.recv() {
            Ok(Ok(())) => Ok(BuildSession { commands, worker: Some(worker) }),
            Ok(Err(e)) => Err((Status::InvalidBlock, e)),
            Err(_) => Err((Status::Internal, "the build thread ended before starting".into())),
        }
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

fn retain_purge_candidate(candidates: &mut BinaryHeap<(u64, B256, usize)>, candidate: (u64, B256, usize)) {
    candidates.push(candidate);
    if candidates.len() > MAX_PURGE_KEYS {
        candidates.pop();
    }
}

#[cfg(test)]
mod purge_candidate_tests {
    use super::*;

    #[test]
    fn a_large_expiry_tie_retains_only_the_first_keys_and_their_costs() {
        let mut candidates = BinaryHeap::new();
        for key in (0..10_000u64).rev() {
            let mut bytes = [0u8; 32];
            bytes[24..].copy_from_slice(&key.to_be_bytes());
            retain_purge_candidate(&mut candidates, (1, B256::from(bytes), key as usize));
            assert!(candidates.len() <= MAX_PURGE_KEYS);
        }
        let kept = candidates.into_sorted_vec();
        assert_eq!(kept.len(), MAX_PURGE_KEYS);
        for (key, (_, _, cost)) in kept.iter().enumerate() {
            assert_eq!(*cost, key);
        }
    }
}
