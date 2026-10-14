//! The trie nodes each persist wrote, queued for the C# shell to replay what `PersistenceManager.PersistSnapshot`
//! (`Nethermind.State.Flat/PersistenceManager.cs`) does to the persisted `TrieNode`s: `IsPersisted = true` and
//! `PrunePersistedRecursively(1)`.
//!
//! The C# node of an entry is the one its shell handed over for that address, path and hash; the persisted range lets
//! the shell release what it keeps for the states the persist covered.
//!
//! With long finality, converting a base into the persisted-snapshot tier marks its nodes the same way
//! (`PersistedSnapshotBuilder.cs`), queued as a [`EventKind::Converted`] entry in the same queue. Writing a persisted
//! snapshot to the persistence marks nothing; its entry has no nodes and only releases the range.

use std::collections::VecDeque;

use parking_lot::Mutex;

use crate::types::{Hash256, StateId, TreePath};

/// A trie node a persist wrote: a state trie node when `address_hash` is `None`, else a node of that account's storage
/// trie. Hash-only placeholders are never written, so never listed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PersistedNode {
    pub address_hash: Option<Hash256>,
    pub path: TreePath,
    pub hash: Hash256,
}

/// What wrote the nodes of a [`PersistedNodes`].
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EventKind {
    /// A persist to the persistence.
    Persisted = 0,
    /// A conversion of a base into the persisted-snapshot tier.
    Converted = 1,
}

/// The nodes of the snapshot persisted or converted from `from` to `to`, in the order they were written.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PersistedNodes {
    pub kind: EventKind,
    pub from: StateId,
    pub to: StateId,
    pub nodes: Vec<PersistedNode>,
}

/// The queue of persisted nodes, unbounded while enabled: dropping an entry would leave its C# nodes unpruned.
pub struct PersistedNodeEvents {
    enabled: bool,
    queue: Mutex<VecDeque<PersistedNodes>>,
}

impl PersistedNodeEvents {
    /// A queue that records when `enabled`, which a host draining it sets; otherwise nothing is kept.
    pub fn new(enabled: bool) -> Self {
        Self {
            enabled,
            queue: Mutex::default(),
        }
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    pub(crate) fn push(&self, event: PersistedNodes) {
        if self.enabled {
            self.queue.lock().push_back(event);
        }
    }

    /// Takes up to `max` persists, oldest first.
    pub fn drain(&self, max: usize) -> Vec<PersistedNodes> {
        let mut queue = self.queue.lock();
        let count = max.min(queue.len());
        queue.drain(..count).collect()
    }

    /// The persists queued.
    pub fn len(&self) -> usize {
        self.queue.lock().len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

#[cfg(test)]
mod tests {
    use super::{EventKind, PersistedNode, PersistedNodeEvents, PersistedNodes};
    use crate::types::{StateId, TreePath};

    fn persist(block: u64) -> PersistedNodes {
        PersistedNodes {
            kind: EventKind::Persisted,
            from: StateId::new(block - 1, [0; 32]),
            to: StateId::new(block, [0; 32]),
            nodes: vec![PersistedNode {
                address_hash: None,
                path: TreePath::from_hex("ab"),
                hash: [block as u8; 32],
            }],
        }
    }

    #[test]
    fn events_drain_oldest_first_and_only_when_enabled() {
        let events = PersistedNodeEvents::new(true);
        events.push(persist(1));
        events.push(persist(2));
        events.push(persist(3));

        assert_eq!(events.drain(2), [persist(1), persist(2)]);
        assert_eq!(events.len(), 1);
        assert_eq!(events.drain(10), [persist(3)]);
        assert!(events.is_empty());

        let disabled = PersistedNodeEvents::new(false);
        disabled.push(persist(1));
        assert!(disabled.is_empty());
    }
}
