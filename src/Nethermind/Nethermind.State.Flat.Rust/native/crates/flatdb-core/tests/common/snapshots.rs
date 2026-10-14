//! Snapshot-layer test support: state ids, snapshot builders, a dictionary persistence reader and finality providers.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use flatdb_core::bundle::ReadOnlySnapshotBundle;
use flatdb_core::kv::ReadFlags;
use flatdb_core::persistence::{EmptyIterator, PersistenceReader, PersistenceResult};
use flatdb_core::repository::StateHeaderProvider;
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};
use hashbrown::HashMap;

/// `CreateStateId` of the repository and compactor tests: the root is zero but for its first byte.
pub fn state_id(block_number: u64, root_byte: u8) -> StateId {
    let mut root = [0u8; 32];
    root[0] = root_byte;
    StateId::new(block_number, root)
}

pub fn address(byte: u8) -> Address {
    [byte; 20]
}

/// An address whose byte order is the order of `index`.
pub fn indexed_address(index: u32) -> Address {
    let mut address = [0u8; 20];
    address[16..].copy_from_slice(&index.to_be_bytes());
    address
}

pub fn snapshot(from: StateId, to: StateId, fill: impl FnOnce(&mut SnapshotContentBuilder)) -> Arc<Snapshot> {
    let mut builder = SnapshotContentBuilder::new();
    fill(&mut builder);
    Arc::new(Snapshot::new(from, to, builder.build()))
}

/// The slot a caller reads that passes the newest clear of `address` as its self-destruct index, as
/// `FlatStateReader` does: through the bundle's slot filter when `filtered`.
pub fn read_slot<R: PersistenceReader>(
    bundle: &ReadOnlySnapshotBundle<R>,
    address: &Address,
    slot: &U256,
    filtered: bool,
) -> PersistenceResult<Option<U256>> {
    let self_destruct_snapshot_idx = bundle.determine_self_destruct_snapshot_idx(address);
    let (value, _) = if filtered {
        bundle.get_slot_filtered(address, slot, self_destruct_snapshot_idx)?
    } else {
        bundle.get_slot(address, slot, self_destruct_snapshot_idx)?
    };
    Ok(value)
}

pub fn empty_snapshot(from: StateId, to: StateId) -> Arc<Snapshot> {
    snapshot(from, to, |_| {})
}

/// Persistence over dictionaries, counting slot reads.
#[derive(Default)]
pub struct DictionaryReader {
    pub accounts: HashMap<Address, Account>,
    pub slots: HashMap<(Address, U256), U256>,
    pub state_nodes: HashMap<TreePath, Vec<u8>>,
    pub storage_nodes: HashMap<(Hash256, TreePath), Vec<u8>>,
    pub current_state: Option<StateId>,
    pub slot_reads: AtomicUsize,
}

impl DictionaryReader {
    pub fn slot_reads(&self) -> usize {
        self.slot_reads.load(Ordering::SeqCst)
    }
}

impl PersistenceReader for DictionaryReader {
    type Iter = EmptyIterator;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        Ok(self.accounts.get(address).copied())
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        self.slot_reads.fetch_add(1, Ordering::SeqCst);
        Ok(self.slots.get(&(*address, *slot)).copied())
    }

    fn current_state(&self) -> StateId {
        self.current_state.unwrap_or(StateId::PRE_GENESIS)
    }

    fn load_state_rlp<R>(
        &self,
        path: &TreePath,
        _flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> PersistenceResult<Option<R>> {
        Ok(self.state_nodes.get(path).map(|rlp| read(rlp)))
    }

    fn load_storage_rlp<R>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        _flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> PersistenceResult<Option<R>> {
        Ok(self.storage_nodes.get(&(*address_hash, *path)).map(|rlp| read(rlp)))
    }

    fn get_account_raw(&self, _address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        Ok(None)
    }

    fn try_get_storage_raw(&self, _address_hash: &Hash256, _slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        Ok(None)
    }

    fn create_account_iterator(&self, _start_key: &Hash256, _end_key: &Hash256) -> PersistenceResult<EmptyIterator> {
        Ok(EmptyIterator)
    }

    fn create_storage_iterator(
        &self,
        _account_key: &Hash256,
        _start_slot_key: &Hash256,
        _end_slot_key: &Hash256,
    ) -> PersistenceResult<EmptyIterator> {
        Ok(EmptyIterator)
    }

    fn is_preimage_mode(&self) -> bool {
        false
    }
}

/// Finality as a map of canonical roots at or below a finalized block.
#[derive(Default)]
pub struct FixedFinality {
    pub finalized_block_number: u64,
    pub roots: HashMap<u64, Hash256>,
}

impl StateHeaderProvider for FixedFinality {
    fn finalized_block_number(&self) -> u64 {
        self.finalized_block_number
    }

    fn finalized_state_root(&self, block_number: u64) -> Option<Hash256> {
        if block_number > self.finalized_block_number {
            return None;
        }
        self.roots.get(&block_number).copied()
    }
}

/// Finality a test moves, as the C# persistence manager tests' `TestFinalizedStateProvider`: a finalized block, roots
/// set per height and served only up to a lookup ceiling, and the lookups counted per height.
#[derive(Default)]
pub struct SettableFinality {
    state: std::sync::Mutex<SettableFinalityState>,
}

#[derive(Default)]
struct SettableFinalityState {
    finalized_block_number: u64,
    roots: HashMap<u64, Hash256>,
    root_lookup_ceiling: Option<u64>,
    lookups: HashMap<u64, usize>,
}

impl SettableFinality {
    pub fn set_finalized_block_number(&self, block_number: u64) {
        self.state.lock().unwrap().finalized_block_number = block_number;
    }

    pub fn set_finalized_state_root_at(&self, block_number: u64, root: Hash256) {
        self.state.lock().unwrap().roots.insert(block_number, root);
    }

    /// Roots above `ceiling` are not served; `None` serves every root.
    pub fn set_root_lookup_ceiling(&self, ceiling: Option<u64>) {
        self.state.lock().unwrap().root_lookup_ceiling = ceiling;
    }

    pub fn lookup_count(&self, block_number: u64) -> usize {
        self.state
            .lock()
            .unwrap()
            .lookups
            .get(&block_number)
            .copied()
            .unwrap_or(0)
    }
}

impl StateHeaderProvider for SettableFinality {
    fn finalized_block_number(&self) -> u64 {
        self.state.lock().unwrap().finalized_block_number
    }

    fn finalized_state_root(&self, block_number: u64) -> Option<Hash256> {
        let mut state = self.state.lock().unwrap();
        *state.lookups.entry(block_number).or_default() += 1;
        if state.root_lookup_ceiling.is_some_and(|ceiling| block_number > ceiling) {
            return None;
        }
        state.roots.get(&block_number).copied()
    }
}
