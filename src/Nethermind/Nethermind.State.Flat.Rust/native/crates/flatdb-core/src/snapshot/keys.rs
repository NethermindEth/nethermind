//! Keys of snapshot content and their sort orders: port of `Nethermind.State.Flat/Collections/SnapshotKeyComparers.cs`
//! and of the `HashedKey<...>` key shapes `Snapshot.cs` uses.
//!
//! Lookups match keys by equality, as the C# dictionaries do with `Equals`; sorting and merging order them by
//! [`SnapshotKey::compare`], the comparer each C# struct implements. Trie-node order matches the persistence layer's.

use std::cmp::Ordering;
use std::hash::Hash;

use crate::types::{Address, Hash256, TreePath, U256};

/// A slot key, `HashedKey<(Address, UInt256)>`.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct StorageKey {
    pub address: Address,
    pub slot: U256,
}

impl StorageKey {
    pub const fn new(address: Address, slot: U256) -> Self {
        Self { address, slot }
    }
}

/// A storage trie node key, `HashedKey<(Hash256, TreePath)>`: the account path and the node path.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct StorageNodeKey {
    pub address_hash: Hash256,
    pub path: TreePath,
}

impl StorageNodeKey {
    pub const fn new(address_hash: Hash256, path: TreePath) -> Self {
        Self { address_hash, path }
    }
}

/// The number of shards a mutable content splits its keyed maps into, each under its own lock.
pub(crate) const SHARDS: usize = 16;

/// The shard of a key: the low nibble of the first byte of the address, or of the account path for a storage node.
#[cfg(feature = "sharded-freeze")]
pub(crate) trait ShardKey: SnapshotKey {
    fn shard(&self) -> usize;
}

pub(crate) const fn shard_of(first_byte: u8) -> usize {
    (first_byte & 0x0F) as usize
}

#[cfg(feature = "sharded-freeze")]
impl ShardKey for StorageKey {
    fn shard(&self) -> usize {
        shard_of(self.address[0])
    }
}

#[cfg(feature = "sharded-freeze")]
impl ShardKey for StorageNodeKey {
    fn shard(&self) -> usize {
        shard_of(self.address_hash[0])
    }
}

/// A key of snapshot content with the sort order of its C# comparer.
pub trait SnapshotKey: Copy + Eq + Hash + Send + Sync + 'static {
    fn compare(&self, other: &Self) -> Ordering;
}

/// `AddressKeyComparer`: byte-wise.
impl SnapshotKey for Address {
    fn compare(&self, other: &Self) -> Ordering {
        self.cmp(other)
    }
}

/// `StorageKeyComparer`: the address byte-wise, then the slot by value.
impl SnapshotKey for StorageKey {
    fn compare(&self, other: &Self) -> Ordering {
        self.address
            .cmp(&other.address)
            .then_with(|| self.slot.cmp(&other.slot))
    }
}

/// `StateNodeKeyComparer`: [`compare_tree_paths`].
impl SnapshotKey for TreePath {
    fn compare(&self, other: &Self) -> Ordering {
        compare_tree_paths(self, other)
    }
}

/// `StorageNodeKeyComparer`: the account path byte-wise, then [`compare_tree_paths`].
impl SnapshotKey for StorageNodeKey {
    fn compare(&self, other: &Self) -> Ordering {
        self.address_hash
            .cmp(&other.address_hash)
            .then_with(|| compare_tree_paths(&self.path, &other.path))
    }
}

/// Port of `TreePath.CompareTo`: the nibbles both paths have, then the shorter path first.
///
/// Unlike [`TreePath`]'s equality it ignores the bytes past each length, so two paths differing only there compare
/// equal while being different keys, exactly as in C#.
pub fn compare_tree_paths(left: &TreePath, right: &TreePath) -> Ordering {
    let min_length = usize::from(left.length.min(right.length));
    let common_byte_length = min_length / 2;
    let by_byte = left.path[..common_byte_length].cmp(&right.path[..common_byte_length]);
    if by_byte != Ordering::Equal {
        return by_byte;
    }

    if min_length & 1 != 0 {
        let by_nibble = (left.path[common_byte_length] & 0xF0).cmp(&(right.path[common_byte_length] & 0xF0));
        if by_nibble != Ordering::Equal {
            return by_nibble;
        }
    }

    left.length.cmp(&right.length)
}
