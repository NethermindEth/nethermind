//! Port of the read-only bundle cache of `Nethermind.State.Flat/FlatDbManager.cs` (`_readonlySnapshotBundleCache`):
//! assembled bundles shared per base state until the next clear.
//!
//! The manager clears it every 15 seconds, after a persist moves the persisted state or removes base snapshots, after
//! a flush, and after dropping unreachable states; the cache only holds the entries. A cached entry is one lease on its
//! bundle; [`BundleCache::get`] hands out another.

use std::sync::Arc;

use hashbrown::HashMap;
use hashbrown::hash_map::Entry;
use parking_lot::RwLock;

use crate::types::StateId;

pub struct BundleCache<B> {
    bundles: RwLock<HashMap<StateId, Arc<B>>>,
}

impl<B> Default for BundleCache<B> {
    fn default() -> Self {
        Self {
            bundles: RwLock::default(),
        }
    }
}

impl<B> BundleCache<B> {
    pub fn new() -> Self {
        Self::default()
    }

    /// A lease on the bundle cached for `state_id`.
    pub fn get(&self, state_id: &StateId) -> Option<Arc<B>> {
        self.bundles.read().get(state_id).cloned()
    }

    /// Caches `bundle` for `state_id` unless a bundle is cached there already (`TryAdd`); the rejected lease is
    /// dropped.
    pub fn try_add(&self, state_id: StateId, bundle: Arc<B>) -> bool {
        match self.bundles.write().entry(state_id) {
            Entry::Occupied(_) => false,
            Entry::Vacant(entry) => {
                entry.insert(bundle);
                true
            }
        }
    }

    /// Port of `ClearReadOnlyBundleCache`: drops every cached lease, outside the lock.
    pub fn clear(&self) {
        let removed = std::mem::take(&mut *self.bundles.write());
        drop(removed);
    }

    pub fn len(&self) -> usize {
        self.bundles.read().len()
    }

    pub fn is_empty(&self) -> bool {
        self.bundles.read().is_empty()
    }
}
