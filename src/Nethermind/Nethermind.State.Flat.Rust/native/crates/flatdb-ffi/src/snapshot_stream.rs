//! The record stream of `fdb_bld_append`: the content of a C# `Snapshot`, serialized by
//! `Nethermind.State.Flat.Rust/SnapshotSerializer.cs` and handed over a chunk at a time.
//!
//! Each record is a tag byte followed by its fields, encoded as in [`crate::op_stream`]; a later record of a key
//! replaces the earlier one, as a write to the C# dictionaries does. Tests also get a snapshot's content back as
//! records ([`dump`]).

#[cfg(any(test, feature = "test-api"))]
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
#[cfg(any(test, feature = "test-api"))]
use flatdb_core::snapshot::keys::SnapshotKey;

use crate::abi::FfiError;
use crate::op_stream::Reader;
#[cfg(any(test, feature = "test-api"))]
use crate::op_stream::writer::OpWriter;

/// `Accounts[address] = account`: address, account.
pub const SET_ACCOUNT: u8 = 0;
/// `Accounts[address] = null`: address.
pub const DELETE_ACCOUNT: u8 = 1;
/// `Storages[(address, slot)] = value`: address, slot, value.
pub const SET_SLOT: u8 = 2;
/// `Storages[(address, slot)] = null`, a zero write: address, slot.
pub const DELETE_SLOT: u8 = 3;
/// `SelfDestructedStorageAddresses[address] = isNewAccount`: address, a byte that is 1 for a new account.
pub const SELF_DESTRUCT: u8 = 4;
/// `StateNodes[path] = node`: path, hash, RLP; empty RLP is a hash-only placeholder.
pub const STATE_NODE: u8 = 5;
/// `StorageNodes[(addressHash, path)] = node`: address hash, path, hash, RLP; empty RLP is a hash-only placeholder.
pub const STORAGE_NODE: u8 = 6;

/// Adds every record of `records` to `builder`, in order. A malformed record stops the stream; the ones before it stay
/// added.
pub fn append(builder: &mut SnapshotContentBuilder, records: &[u8]) -> Result<(), FfiError> {
    let mut reader = Reader::new(records);
    let mut index = 0usize;
    while !reader.is_done() {
        let start = reader.position;
        append_one(builder, &mut reader).map_err(|error| FfiError {
            status: error.status,
            message: format!("{} (record {index} at byte {start})", error.message),
        })?;
        index += 1;
    }
    Ok(())
}

fn append_one(builder: &mut SnapshotContentBuilder, reader: &mut Reader<'_>) -> Result<(), FfiError> {
    match reader.u8()? {
        SET_ACCOUNT => {
            let address = reader.address()?;
            builder.set_account(address, Some(reader.account()?));
        }
        DELETE_ACCOUNT => builder.set_account(reader.address()?, None),
        SET_SLOT => {
            let (address, slot, value) = (reader.address()?, reader.u256()?, reader.u256()?);
            builder.set_storage(address, slot, Some(value));
        }
        DELETE_SLOT => {
            let (address, slot) = (reader.address()?, reader.u256()?);
            builder.set_storage(address, slot, None);
        }
        SELF_DESTRUCT => {
            let address = reader.address()?;
            builder.set_self_destruct(address, reader.u8()? != 0);
        }
        STATE_NODE => {
            let (path, hash) = (reader.path()?, reader.hash()?);
            builder.set_state_node(path, hash, reader.bytes()?);
        }
        STORAGE_NODE => {
            let (address_hash, path, hash) = (reader.hash()?, reader.path()?, reader.hash()?);
            builder.set_storage_node(address_hash, path, hash, reader.bytes()?);
        }
        tag => return Err(FfiError::invalid_argument(format!("unknown record tag {tag}"))),
    }
    Ok(())
}

/// Every entry of `snapshot` as records: its accounts, clears, slots, state trie nodes, then storage trie nodes, each
/// kind sorted as the C# comparers sort its keys, so equal contents dump to equal bytes.
#[cfg(any(test, feature = "test-api"))]
pub fn dump(snapshot: &Snapshot) -> Vec<u8> {
    let mut writer = OpWriter::default();
    for (address, account) in sorted(snapshot.accounts()) {
        match account {
            Some(account) => writer.op(SET_ACCOUNT).address(address).account(account),
            None => writer.op(DELETE_ACCOUNT).address(address),
        };
    }
    for (address, is_new_account) in sorted(snapshot.self_destructed_storage_addresses()) {
        writer.op(SELF_DESTRUCT).address(address).byte(is_new_account.into());
    }
    for (key, value) in sorted(snapshot.storages()) {
        match value {
            Some(value) => writer.op(SET_SLOT).address(&key.address).u256(&key.slot).u256(value),
            None => writer.op(DELETE_SLOT).address(&key.address).u256(&key.slot),
        };
    }
    for (path, node) in sorted(snapshot.state_nodes()) {
        writer.op(STATE_NODE).path(path).hash(node.hash).bytes(node.rlp);
    }
    for (key, node) in sorted(snapshot.storage_nodes()) {
        writer
            .op(STORAGE_NODE)
            .hash(&key.address_hash)
            .path(&key.path)
            .hash(node.hash)
            .bytes(node.rlp);
    }
    writer.0
}

#[cfg(any(test, feature = "test-api"))]
fn sorted<'a, K: SnapshotKey, V>(entries: impl Iterator<Item = (&'a K, V)>) -> Vec<(&'a K, V)> {
    let mut entries: Vec<_> = entries.collect();
    entries.sort_unstable_by(|left, right| left.0.compare(right.0));
    entries
}
