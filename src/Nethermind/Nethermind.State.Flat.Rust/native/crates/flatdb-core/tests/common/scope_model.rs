//! A plain model of snapshot layers over small keys, for the mutable-content and scope-bundle proptests: every key is
//! a small index into a fixed table, so the model's maps are ordered and failures print readably.

use std::collections::BTreeMap;
use std::sync::Arc;

use flatdb_core::keccak::account_path;
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::types::{Account, Address, EMPTY_TREE_HASH, Hash256, KECCAK_OF_EMPTY_STRING, StateId, TreePath, U256};
use proptest::prelude::*;

use super::snapshots::{DictionaryReader, address};

pub const ADDRESSES: u8 = 6;
pub const SLOTS: u8 = 4;
/// Paths of several lengths, the empty root included.
pub const PATHS: [&str; 6] = ["", "1", "a", "12", "ab3", "fff0"];
/// Hash 0 is the zero hash of an inline node; the others are `[h; 32]`.
pub const HASHES: u8 = 4;

/// An account as its nonce and whether it has storage.
pub type ModelAccount = (u64, bool);

/// Address `a`, whose first byte puts it in shard `a`.
pub fn model_address(a: u8) -> Address {
    address(a * 0x11)
}

pub fn model_address_hash(a: u8) -> Hash256 {
    account_path(&model_address(a))
}

pub fn model_slot(k: u8) -> U256 {
    U256::from_u64(u64::from(k))
}

pub fn model_path(p: u8) -> TreePath {
    TreePath::from_hex(PATHS[usize::from(p)])
}

pub fn model_hash(h: u8) -> Hash256 {
    [h; 32]
}

pub fn model_account((nonce, has_storage): ModelAccount) -> Account {
    let storage_root = if has_storage { [7; 32] } else { EMPTY_TREE_HASH };
    Account::with_hashes(nonce, U256::from_u64(nonce * 10), storage_root, KECCAK_OF_EMPTY_STRING)
}

fn index_of<T: PartialEq>(count: u8, value: &T, of: impl Fn(u8) -> T) -> u8 {
    (0..count)
        .find(|index| of(*index) == *value)
        .expect("a key of the model")
}

/// The writes of one snapshot. Node values are a hash index and RLP; empty RLP is a placeholder.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Layer {
    pub accounts: BTreeMap<u8, Option<ModelAccount>>,
    pub slots: BTreeMap<(u8, u8), Option<u64>>,
    pub clears: BTreeMap<u8, bool>,
    pub state_nodes: BTreeMap<u8, (u8, Vec<u8>)>,
    pub storage_nodes: BTreeMap<(u8, u8), (u8, Vec<u8>)>,
}

impl Layer {
    pub fn snapshot(&self, from: StateId, to: StateId) -> Arc<Snapshot> {
        let mut builder = SnapshotContentBuilder::new();
        for (a, account) in &self.accounts {
            builder.set_account(model_address(*a), account.map(model_account));
        }
        for ((a, k), value) in &self.slots {
            builder.set_storage(model_address(*a), model_slot(*k), value.map(U256::from_u64));
        }
        for (a, is_new) in &self.clears {
            builder.set_self_destruct(model_address(*a), *is_new);
        }
        for (p, (h, rlp)) in &self.state_nodes {
            builder.set_state_node(model_path(*p), model_hash(*h), rlp);
        }
        for ((a, p), (h, rlp)) in &self.storage_nodes {
            builder.set_storage_node(model_address_hash(*a), model_path(*p), model_hash(*h), rlp);
        }
        Arc::new(Snapshot::new(from, to, builder.build()))
    }

    /// What `snapshot` holds, in the model's keys.
    pub fn of(snapshot: &Snapshot) -> Self {
        let address_index = |address: &Address| index_of(ADDRESSES, address, model_address);
        let path_index = |path: &TreePath| index_of(PATHS.len() as u8, path, model_path);
        Self {
            accounts: snapshot
                .accounts()
                .map(|(address, account)| {
                    let account = account.map(|account| (account.nonce, account.has_storage()));
                    (address_index(address), account)
                })
                .collect(),
            slots: snapshot
                .storages()
                .map(|(key, value)| {
                    let k = key.slot.0[0] as u8;
                    ((address_index(&key.address), k), value.map(|value| value.0[0]))
                })
                .collect(),
            clears: snapshot
                .self_destructed_storage_addresses()
                .map(|(address, is_new)| (address_index(address), is_new))
                .collect(),
            state_nodes: snapshot
                .state_nodes()
                .map(|(path, node)| (path_index(path), (node.hash[0], node.rlp.to_vec())))
                .collect(),
            storage_nodes: snapshot
                .storage_nodes()
                .map(|(key, node)| {
                    let a = index_of(ADDRESSES, &key.address_hash, model_address_hash);
                    ((a, path_index(&key.path)), (node.hash[0], node.rlp.to_vec()))
                })
                .collect(),
        }
    }
}

/// The persisted state: accounts, slots (zero included) and trie node RLP.
#[derive(Clone, Debug, Default)]
pub struct Persisted {
    pub accounts: BTreeMap<u8, ModelAccount>,
    pub slots: BTreeMap<(u8, u8), u64>,
    pub state_nodes: BTreeMap<u8, Vec<u8>>,
    pub storage_nodes: BTreeMap<(u8, u8), Vec<u8>>,
}

impl Persisted {
    pub fn reader(&self) -> DictionaryReader {
        DictionaryReader {
            accounts: self
                .accounts
                .iter()
                .map(|(a, account)| (model_address(*a), model_account(*account)))
                .collect(),
            slots: self
                .slots
                .iter()
                .map(|((a, k), value)| ((model_address(*a), model_slot(*k)), U256::from_u64(*value)))
                .collect(),
            state_nodes: self
                .state_nodes
                .iter()
                .map(|(p, rlp)| (model_path(*p), rlp.clone()))
                .collect(),
            storage_nodes: self
                .storage_nodes
                .iter()
                .map(|((a, p), rlp)| ((model_address_hash(*a), model_path(*p)), rlp.clone()))
                .collect(),
            ..Default::default()
        }
    }
}

pub fn account_strategy() -> impl Strategy<Value = ModelAccount> {
    (1u64..50, any::<bool>())
}

/// A node: a hash index and RLP, empty a quarter of the time (a placeholder).
pub fn node_strategy() -> impl Strategy<Value = (u8, Vec<u8>)> {
    (
        0..HASHES,
        prop_oneof![1 => Just(Vec::new()), 3 => prop::collection::vec(any::<u8>(), 1..4)],
    )
}

pub fn layer_strategy() -> impl Strategy<Value = Layer> {
    (
        prop::collection::btree_map(0..ADDRESSES, prop::option::of(account_strategy()), 0..3),
        prop::collection::btree_map((0..ADDRESSES, 0..SLOTS), prop::option::of(1u64..100), 0..6),
        prop::collection::btree_map(0..ADDRESSES, any::<bool>(), 0..2),
        prop::collection::btree_map(0..PATHS.len() as u8, node_strategy(), 0..3),
        prop::collection::btree_map((0..ADDRESSES, 0..PATHS.len() as u8), node_strategy(), 0..3),
    )
        .prop_map(|(accounts, slots, clears, state_nodes, storage_nodes)| Layer {
            accounts,
            slots,
            clears,
            state_nodes,
            storage_nodes,
        })
}

pub fn persisted_strategy() -> impl Strategy<Value = Persisted> {
    let rlp = || prop::collection::vec(any::<u8>(), 1..4);
    (
        prop::collection::btree_map(0..ADDRESSES, account_strategy(), 0..5),
        prop::collection::btree_map((0..ADDRESSES, 0..SLOTS), 0u64..100, 0..12),
        prop::collection::btree_map(0..PATHS.len() as u8, rlp(), 0..4),
        prop::collection::btree_map((0..ADDRESSES, 0..PATHS.len() as u8), rlp(), 0..6),
    )
        .prop_map(|(accounts, slots, state_nodes, storage_nodes)| Persisted {
            accounts,
            slots,
            state_nodes,
            storage_nodes,
        })
}
