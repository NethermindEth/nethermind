//! The record stream of `fdb_bld_append`: the content of a C# `Snapshot`, serialized by
//! `Nethermind.State.Flat.Rust/SnapshotSerializer.cs` and handed over a chunk at a time.
//!
//! Each record is a tag byte followed by its fields, encoded as in [`crate::op_stream`]; a later record of a key
//! replaces the earlier one, as a write to the C# dictionaries does.

use flatdb_core::snapshot::content::SnapshotContentBuilder;

use crate::abi::FfiError;
use crate::op_stream::Reader;

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
