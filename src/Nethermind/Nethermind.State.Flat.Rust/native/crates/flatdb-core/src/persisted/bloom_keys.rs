//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotBloomBuilder.cs`: the keys a persisted snapshot's
//! bloom holds, and the rebuild of a bloom from a table; and of `Persistence/BloomFilter/RefCountedBloomFilter.cs`, the
//! bloom one or more snapshots share.
//!
//! One filter covers every read: an address key (the first 8 bytes of the address, little-endian) gates the account,
//! the self-destruct flag and the address half of every slot read; a slot key XORs the folded big-endian slot into it;
//! trie-node keys fold the path in its on-disk encoding, so they hash the same whether a path arrives with a dirty tail
//! or decoded from a table.

use std::sync::Arc;
use std::sync::atomic::Ordering;

use super::io::ByteReader;
use super::keys::{self, ADDRESS_HASH_PREFIX_LENGTH};
use super::scanner::Scanner;
use super::{TierError, TierResult};
use crate::bloom::BloomFilter;
use crate::stats::TierCounters;
use crate::types::TreePath;

/// `RefCountedBloomFilter`: a snapshot's bloom, shared through its `Arc` when a large compaction lends it to the
/// snapshots it contains. It counts in `PersistedSnapshotBloomMemory` and `PersistedSnapshotBloomCount` while it lives.
pub struct PersistedBloom {
    filter: BloomFilter,
    counters: Arc<TierCounters>,
}

impl PersistedBloom {
    pub fn new(filter: BloomFilter, counters: Arc<TierCounters>) -> Arc<Self> {
        counters
            .bloom_memory
            .fetch_add(filter.data_bytes() as i64, Ordering::Relaxed);
        counters.bloom_count.fetch_add(1, Ordering::Relaxed);
        Arc::new(Self { filter, counters })
    }

    /// `AlwaysTrue`: the placeholder of a snapshot whose bloom is built later, or of a tier without blooms.
    pub fn always_true(counters: Arc<TierCounters>) -> Arc<Self> {
        Self::new(BloomFilter::always_true(), counters)
    }

    pub fn filter(&self) -> &BloomFilter {
        &self.filter
    }
}

impl Drop for PersistedBloom {
    fn drop(&mut self) {
        self.counters
            .bloom_memory
            .fetch_sub(self.filter.data_bytes() as i64, Ordering::Relaxed);
        self.counters.bloom_count.fetch_sub(1, Ordering::Relaxed);
    }
}

fn u64_at(bytes: &[u8], at: usize) -> u64 {
    u64::from_le_bytes(bytes[at..at + 8].try_into().expect("8 bytes"))
}

/// `AddressKey`.
pub fn address_key(address: &[u8]) -> u64 {
    u64_at(address, 0)
}

/// `Fold32`: the XOR of the four little-endian words of the first 32 bytes.
fn fold32(bytes: &[u8]) -> u64 {
    u64_at(bytes, 0) ^ u64_at(bytes, 8) ^ u64_at(bytes, 16) ^ u64_at(bytes, 24)
}

/// `SlotKey`, with the slot big-endian.
pub fn slot_key(address_key: u64, slot_be: &[u8]) -> u64 {
    address_key ^ fold32(slot_be)
}

/// `StatePathKey(TreePath)`: the key of the path's on-disk encoding.
pub fn state_path_key(path: &TreePath) -> u64 {
    let (_, encoded) = keys::encode_path(path);
    state_path_key_of_encoded(encoded.as_slice())
}

/// `StatePathKey(ReadOnlySpan<byte>)`: the key of an encoded path, zero-padded to 33 bytes.
pub fn state_path_key_of_encoded(encoded: &[u8]) -> u64 {
    let mut padded = [0u8; 33];
    padded[..encoded.len()].copy_from_slice(encoded);
    fold32(&padded) ^ u64::from(padded[32])
}

/// `StorageNodeKey`: the first 8 bytes of the address hash XOR the path key.
pub fn storage_node_key(address_hash: &[u8], path: &TreePath) -> u64 {
    u64_at(address_hash, 0) ^ state_path_key(path)
}

/// [`storage_node_key`] of a storage-node table key.
pub fn storage_node_key_of_table_key(key: &[u8]) -> u64 {
    u64_at(keys::storage_address_hash(key), 0) ^ state_path_key_of_encoded(keys::storage_path_bytes(key))
}

/// `Build`: a bloom sized for the keys of the table `reader` covers, then filled with them: one address key per
/// address, one per slot, one per trie node. A table without keys gets a capacity of 1.
pub fn rebuild(reader: &ByteReader<'_>, bits_per_key: f64) -> TierResult<BloomFilter> {
    let scanner = Scanner::new(reader);
    let mut capacity = 0i64;
    let mut per_addresses = scanner.per_addresses();
    while per_addresses.next_address()?.is_some() {
        capacity += 1;
        while per_addresses.next_slot()?.is_some() {
            capacity += 1;
        }
    }
    let mut state_nodes = scanner.state_nodes();
    while state_nodes.next_node()?.is_some() {
        capacity += 1;
    }
    let mut storage_nodes = scanner.storage_nodes();
    while storage_nodes.next_node()?.is_some() {
        capacity += 1;
    }

    let mut bloom = BloomFilter::new(capacity.max(1), bits_per_key)
        .map_err(|error| TierError::InvalidArgument(error.to_string()))?;
    let mut per_addresses = scanner.per_addresses();
    while let Some(entry) = per_addresses.next_address()? {
        let address_key = address_key(&entry.address);
        bloom.add(address_key);
        while let Some(slot) = per_addresses.next_slot()? {
            bloom.add(slot_key(address_key, &slot.slot));
        }
    }
    let mut state_nodes = scanner.state_nodes();
    while let Some(node) = state_nodes.next_node()? {
        bloom.add(state_path_key(&node.path));
    }
    let mut storage_nodes = scanner.storage_nodes();
    while let Some(node) = storage_nodes.next_node()? {
        bloom.add(storage_node_key(
            &node.address_hash[..ADDRESS_HASH_PREFIX_LENGTH],
            &node.path,
        ));
    }
    Ok(bloom)
}

#[cfg(test)]
mod tests {
    use super::{address_key, slot_key, state_path_key, state_path_key_of_encoded, storage_node_key};
    use crate::persisted::keys;
    use crate::types::TreePath;

    #[test]
    fn keys_fold_the_on_disk_encodings() {
        let mut address = [0u8; 20];
        address[..8].copy_from_slice(&0x0807_0605_0403_0201u64.to_le_bytes());
        assert_eq!(address_key(&address), 0x0807_0605_0403_0201);

        let mut slot = [0u8; 32];
        slot[31] = 0x05;
        slot[0] = 0x80;
        assert_eq!(slot_key(1, &slot), 1 ^ 0x80 ^ (0x05 << 56));

        // The 4-byte form keeps a dirty nibble inside its 4 bytes, so it changes the key, and drops the bytes after.
        let clean = TreePath::from_hex("123");
        assert_eq!(
            state_path_key(&clean),
            u64::from_le_bytes([0x12, 0x30, 0x00, 0x03, 0, 0, 0, 0])
        );
        let mut dirty_inside = clean;
        dirty_inside.path[1] = 0x3F;
        let mut dirty_after = clean;
        dirty_after.path[5] = 0xFF;
        assert_ne!(state_path_key(&clean), state_path_key(&dirty_inside));
        assert_eq!(state_path_key(&clean), state_path_key(&dirty_after));
        let fallback = TreePath::new([0x11; 32], 40);
        let (_, encoded) = keys::encode_path(&fallback);
        assert_eq!(state_path_key(&fallback), state_path_key_of_encoded(encoded.as_slice()));
        assert_eq!(
            state_path_key(&fallback),
            40,
            "the four equal words cancel out, leaving the length byte"
        );

        let address_hash = [0x22; 32];
        assert_eq!(
            storage_node_key(&address_hash, &clean),
            0x2222_2222_2222_2222 ^ state_path_key(&clean)
        );
    }
}
