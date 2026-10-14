//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotStack.cs`: the persisted half of a read-only
//! bundle, probing its snapshots newest first, each behind its bloom.
//!
//! The hit labels and skip-time observations C# records around these loops are taken by the bundle that owns the stack.

use super::TierResult;
use super::bloom_keys;
use super::list::PersistedSnapshotList;
use crate::bloom::BloomFilter;
use crate::types::{Account, Address, Hash256, TreePath, U256};

/// The reads of one persisted snapshot the stack probes, each `None` when the snapshot does not hold the key.
pub trait PersistedLayer {
    fn bloom(&self) -> &BloomFilter;

    /// `Some(None)` for a deletion marker.
    fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>>;

    /// `Some(false)` destructed, `Some(true)` new account.
    fn try_get_self_destruct_flag(&self, address: &Address) -> TierResult<Option<bool>>;

    /// `Some(None)` for a deleted slot.
    fn try_get_slot(&self, address: &Address, slot: &U256) -> TierResult<Option<Option<U256>>>;

    fn try_load_state_node_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>>;

    fn try_load_storage_node_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>>;
}

pub struct PersistedSnapshotStack<S> {
    snapshots: PersistedSnapshotList<S>,
}

impl<S: PersistedLayer> PersistedSnapshotStack<S> {
    pub fn new(snapshots: PersistedSnapshotList<S>) -> Self {
        Self { snapshots }
    }

    pub fn len(&self) -> usize {
        self.snapshots.len()
    }

    pub fn is_empty(&self) -> bool {
        self.snapshots.is_empty()
    }

    /// `TryGetAccount`: the newest snapshot holding the address decides, `Some(None)` being a deletion; `None` falls
    /// through to persistence.
    pub fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>> {
        let address_key = bloom_keys::address_key(address);
        for snapshot in self.snapshots.iter().rev() {
            if !snapshot.bloom().might_contain(address_key) {
                continue;
            }
            if let Some(account) = snapshot.try_get_account(address)? {
                return Ok(Some(account));
            }
        }
        Ok(None)
    }

    /// `TryGetSelfDestruct`: the index in the stack of the newest snapshot with a self-destruct record of either kind.
    pub fn try_get_self_destruct(&self, address: &Address) -> TierResult<Option<usize>> {
        let address_key = bloom_keys::address_key(address);
        for (index, snapshot) in self.snapshots.iter().enumerate().rev() {
            if snapshot.bloom().might_contain(address_key) && snapshot.try_get_self_destruct_flag(address)?.is_some() {
                return Ok(Some(index));
            }
        }
        Ok(None)
    }

    /// `TryGetSlot`: newest first, stopping at the self-destruct boundary: once the probe reaches the snapshot at
    /// `self_destruct_index` (an index in the stack, −1 for none) without a hit, the slot reads as cleared, `Some(None)`.
    /// `None` falls through to persistence.
    pub fn try_get_slot(
        &self,
        address: &Address,
        slot: &U256,
        self_destruct_index: isize,
    ) -> TierResult<Option<Option<U256>>> {
        let address_key = bloom_keys::address_key(address);
        let slot_key = bloom_keys::slot_key(address_key, &slot.to_be_bytes());
        for (index, snapshot) in self.snapshots.iter().enumerate().rev() {
            let bloom = snapshot.bloom();
            if bloom.might_contain(address_key)
                && bloom.might_contain(slot_key)
                && let Some(value) = snapshot.try_get_slot(address, slot)?
            {
                return Ok(Some(value));
            }
            if index as isize <= self_destruct_index {
                return Ok(Some(None));
            }
        }
        Ok(None)
    }

    /// `TryLoadStateRlp`.
    pub fn try_load_state_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        let key = bloom_keys::state_path_key(path);
        for snapshot in self.snapshots.iter().rev() {
            if snapshot.bloom().might_contain(key)
                && let Some(rlp) = snapshot.try_load_state_node_rlp(path)?
            {
                return Ok(Some(rlp));
            }
        }
        Ok(None)
    }

    /// `TryLoadStorageRlp`.
    pub fn try_load_storage_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        let key = bloom_keys::storage_node_key(address_hash, path);
        for snapshot in self.snapshots.iter().rev() {
            if snapshot.bloom().might_contain(key)
                && let Some(rlp) = snapshot.try_load_storage_node_rlp(address_hash, path)?
            {
                return Ok(Some(rlp));
            }
        }
        Ok(None)
    }
}
