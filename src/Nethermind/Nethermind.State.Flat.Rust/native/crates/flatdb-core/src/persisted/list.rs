//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotList.cs`: persisted snapshots, oldest first.
//!
//! Each `Arc` is a lease on its snapshot; dropping the list releases them, as `DisposeRecursive` does.

use std::sync::Arc;

pub struct PersistedSnapshotList<S> {
    snapshots: Vec<Arc<S>>,
}

impl<S> PersistedSnapshotList<S> {
    pub fn new(snapshots: Vec<Arc<S>>) -> Self {
        Self { snapshots }
    }

    pub fn empty() -> Self {
        Self::new(Vec::new())
    }

    pub fn len(&self) -> usize {
        self.snapshots.len()
    }

    pub fn is_empty(&self) -> bool {
        self.snapshots.is_empty()
    }

    pub fn push(&mut self, snapshot: Arc<S>) {
        self.snapshots.push(snapshot);
    }

    pub fn iter(&self) -> impl DoubleEndedIterator<Item = &Arc<S>> + ExactSizeIterator {
        self.snapshots.iter()
    }
}
