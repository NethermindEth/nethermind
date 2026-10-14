//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotBuilder.cs`: writes the table of a base
//! persisted snapshot from an in-memory snapshot, its trie-node RLP going to a blob file.
//!
//! The table is a ref-id record for the blob file, every entry of the snapshot under its [`keys`] key, and the metadata
//! (`blob_range`, `from_*`, `to_*`). Hash-only placeholders are not written.
//!
//! Deviation from C#: C# extracts and sorts each kind of entry by its own comparer in three parallel jobs, then emits the
//! kinds in key order; here every entry is keyed once and the keys are sorted. C# emits in strictly ascending key order
//! (its table builder refuses anything else) and writes the blob in emission order, so both write the same table, blob
//! and bloom keys. They differ only where C# fails: a path with a dirty nibble inside its 4- or 8-byte form can sort
//! differently by path than by key, which C# refuses as out of order and the key sort accepts; two keys that collide
//! (paths equal in their encoding, address hashes sharing 20 bytes) fail in both.

use std::io::Write;

use super::codec::BlobRange;
use super::io::TableSink;
use super::keys::{self, ACCOUNT_COLUMN, PersistedKey};
use super::sorted::table_builder::SortedTableBuilder;
use super::storage::blob_writer::BlobWriter;
use super::{TierResult, bloom_keys};
use crate::bloom::BloomFilter;
use crate::codec::account::encode_account;
use crate::codec::slot::{RLP_SLOT_VALUE_BUFFER_SIZE, encode_slot_value};
use crate::events::PersistedNode;
use crate::snapshot::Snapshot;
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

/// What a build reports beside the bytes it wrote.
#[derive(Debug, Default)]
pub struct BuildOutput {
    /// Whether the snapshot cleared any account's storage.
    pub may_have_self_destructs: bool,
    /// The trie nodes written, in the order they were: the nodes C# marks `IsPersisted` when it converts the snapshot.
    pub written_nodes: Vec<PersistedNode>,
}

enum Entry<'s> {
    StorageNode {
        address_hash: Hash256,
        path: TreePath,
        hash: Hash256,
        rlp: &'s [u8],
    },
    StateNode {
        path: TreePath,
        hash: Hash256,
        rlp: &'s [u8],
    },
    Account(Option<Account>),
    SelfDestruct(bool),
    Slot(Option<U256>),
}

/// `EstimateSize`: the snapshot's memory estimate plus 1 KiB, an upper bound of its table that sizes the writers.
pub fn estimate_size(snapshot: &Snapshot) -> i64 {
    snapshot.estimate_memory() + 1024
}

/// `Build`: writes the table of `snapshot` to `sink`, its trie-node RLP through `blob_writer`, and adds its keys to
/// `bloom` exactly as C# does: an address key per address, a key per slot and per trie node.
pub fn build<S: TableSink, W: Write>(
    snapshot: &Snapshot,
    sink: &mut S,
    blob_writer: &mut BlobWriter<W>,
    bloom: &mut BloomFilter,
) -> TierResult<BuildOutput> {
    let mut entries = collect_entries(snapshot);
    entries.sort_unstable_by_key(|(key, _)| *key);

    let mut output = BuildOutput {
        may_have_self_destructs: snapshot.self_destructed_storage_addresses().next().is_some(),
        written_nodes: Vec::new(),
    };
    let mut table = SortedTableBuilder::new(sink);
    table.add(
        keys::ref_id_key(blob_writer.blob_arena_id()).as_slice(),
        &[keys::REF_ID_VALUE],
    )?;

    let mut current_address: Option<(Address, u64)> = None;
    let mut slot_buffer = [0u8; RLP_SLOT_VALUE_BUFFER_SIZE];
    for (key, entry) in &entries {
        let key = key.as_slice();
        let address_key = if key[0] == ACCOUNT_COLUMN {
            let address: Address = keys::per_address_address(key).try_into().expect("20 bytes");
            match current_address {
                Some((current, address_key)) if current == address => address_key,
                _ => {
                    let address_key = bloom_keys::address_key(&address);
                    bloom.add(address_key);
                    current_address = Some((address, address_key));
                    address_key
                }
            }
        } else {
            0
        };

        match entry {
            Entry::StorageNode {
                address_hash,
                path,
                hash,
                rlp,
            } => {
                let node_ref = blob_writer.write_rlp(rlp)?;
                table.add(key, &node_ref.to_bytes())?;
                bloom.add(bloom_keys::storage_node_key(address_hash, path));
                output.written_nodes.push(PersistedNode {
                    address_hash: Some(*address_hash),
                    path: *path,
                    hash: *hash,
                });
            }
            Entry::StateNode { path, hash, rlp } => {
                let node_ref = blob_writer.write_rlp(rlp)?;
                table.add(key, &node_ref.to_bytes())?;
                bloom.add(bloom_keys::state_path_key(path));
                output.written_nodes.push(PersistedNode {
                    address_hash: None,
                    path: *path,
                    hash: *hash,
                });
            }
            Entry::Account(None) => table.add(key, &[keys::ACCOUNT_DELETED_MARKER])?,
            Entry::Account(Some(account)) => table.add(key, &encode_account(account, true))?,
            Entry::SelfDestruct(is_new_account) => table.add(
                key,
                &[if *is_new_account {
                    keys::SELF_DESTRUCT_NEW_MARKER
                } else {
                    keys::SELF_DESTRUCT_DESTRUCTED_MARKER
                }],
            )?,
            Entry::Slot(value) => {
                bloom.add(bloom_keys::slot_key(address_key, keys::slot_key_bytes(key)));
                // A deleted slot keeps an empty value, the absent sentinel.
                let length = value.map_or(0, |value| encode_slot_value(&value, true, &mut slot_buffer));
                table.add(key, &slot_buffer[..length])?;
            }
        }
    }

    write_metadata(&mut table, snapshot.from(), snapshot.to(), blob_writer)?;
    table.build()?;
    Ok(output)
}

fn collect_entries(snapshot: &Snapshot) -> Vec<(PersistedKey, Entry<'_>)> {
    let mut entries = Vec::with_capacity(
        snapshot.accounts_count()
            + snapshot.storages_count()
            + snapshot.state_nodes_count()
            + snapshot.storage_nodes_count(),
    );
    for (key, node) in snapshot.storage_nodes() {
        if !node.is_hash_only_placeholder() {
            entries.push((
                keys::storage_node_key(&key.address_hash, &key.path),
                Entry::StorageNode {
                    address_hash: key.address_hash,
                    path: key.path,
                    hash: *node.hash,
                    rlp: node.rlp,
                },
            ));
        }
    }
    for (path, node) in snapshot.state_nodes() {
        if !node.is_hash_only_placeholder() {
            entries.push((
                keys::state_node_key(path),
                Entry::StateNode {
                    path: *path,
                    hash: *node.hash,
                    rlp: node.rlp,
                },
            ));
        }
    }
    for (address, account) in snapshot.accounts() {
        entries.push((keys::account_key(address), Entry::Account(*account)));
    }
    for (address, is_new_account) in snapshot.self_destructed_storage_addresses() {
        entries.push((keys::self_destruct_key(address), Entry::SelfDestruct(is_new_account)));
    }
    for (key, value) in snapshot.storages() {
        entries.push((
            keys::slot_key(&key.address, &key.slot.to_be_bytes()),
            Entry::Slot(*value),
        ));
    }
    entries
}

/// `WriteMetadata`: the blob range this build wrote (none when it wrote no node), then the range of the snapshot.
fn write_metadata<S: TableSink, W: Write>(
    table: &mut SortedTableBuilder<'_, S>,
    from: StateId,
    to: StateId,
    blob_writer: &BlobWriter<W>,
) -> TierResult<()> {
    let blob_range = if blob_writer.written() > blob_writer.start_offset() {
        BlobRange {
            blob_arena_id: blob_writer.blob_arena_id(),
            offset: blob_writer.start_offset(),
            length: blob_writer.written() - blob_writer.start_offset(),
        }
    } else {
        BlobRange::NONE
    };
    table.add(
        keys::metadata_key(keys::METADATA_BLOB_RANGE).as_slice(),
        &blob_range.to_bytes(),
    )?;
    table.add(
        keys::metadata_key(keys::METADATA_FROM_BLOCK).as_slice(),
        &from.block_number.to_le_bytes(),
    )?;
    table.add(
        keys::metadata_key(keys::METADATA_FROM_HASH).as_slice(),
        &from.state_root,
    )?;
    table.add(
        keys::metadata_key(keys::METADATA_TO_BLOCK).as_slice(),
        &to.block_number.to_le_bytes(),
    )?;
    table.add(keys::metadata_key(keys::METADATA_TO_HASH).as_slice(), &to.state_root)
}
