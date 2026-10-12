//! Port of `Nethermind.State.Flat/Persistence/BaseTriePersistence.cs`: trie nodes in four columns by path length, as
//! [`crate::codec::keys`] lays them out. Most paths are shorter than 16 nibbles and fit the 8-byte keys; the
//! fallback column holds the rest of both tries, partitioned by a leading 0x00 (state) or 0x01 (storage) byte.

use crate::codec::keys::{
    FULL_PATH_LENGTH, FULL_STATE_NODES_KEY_LENGTH, FULL_STORAGE_NODES_KEY_LENGTH, PATH_LENGTH_LENGTH,
    SHORTENED_PATH_LENGTH, SHORTENED_PATH_THRESHOLD, SHORTENED_STORAGE_NODES_KEY_LENGTH, STATE_NODES_TOP_PATH_LENGTH,
    STATE_NODES_TOP_THRESHOLD, STORAGE_HASH_PREFIX_LENGTH, STORAGE_PREFIX_PORTION, encode_full_state_node_key,
    encode_full_storage_node_key, encode_shortened_state_node_key, encode_shortened_storage_node_key,
    encode_state_top_node_key, state_node_key, storage_node_key,
};
use crate::kv::{Column, KvReader, ReadFlags};
use crate::types::{Hash256, TreePath};

use super::base::{ColumnSink, create_storage_range, delete_matching_keys_by_suffix, for_each_between};
use super::{PersistenceError, PersistenceResult};

/// `BaseTriePersistence.Reader.TryLoadStateRlp`.
pub(crate) fn load_state_rlp<R: KvReader, T>(
    reader: &R,
    path: &TreePath,
    flags: ReadFlags,
    read: impl FnOnce(&[u8]) -> T,
) -> PersistenceResult<Option<T>> {
    let key = state_node_key(path);
    Ok(reader.get_with(key.column, key.as_slice(), flags, read)?)
}

/// `BaseTriePersistence.Reader.TryLoadStorageRlp`.
pub(crate) fn load_storage_rlp<R: KvReader, T>(
    reader: &R,
    address_hash: &Hash256,
    path: &TreePath,
    flags: ReadFlags,
    read: impl FnOnce(&[u8]) -> T,
) -> PersistenceResult<Option<T>> {
    let key = storage_node_key(address_hash, path);
    Ok(reader.get_with(key.column, key.as_slice(), flags, read)?)
}

/// `BaseTriePersistence.WriteBatch.SetStateTrieNode`.
pub(crate) fn set_state_trie_node(batch: &mut impl ColumnSink, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
    let key = state_node_key(path);
    batch.put(key.column, key.as_slice(), rlp)?;
    Ok(())
}

/// `BaseTriePersistence.WriteBatch.SetStorageTrieNode`.
pub(crate) fn set_storage_trie_node(
    batch: &mut impl ColumnSink,
    address_hash: &Hash256,
    path: &TreePath,
    rlp: &[u8],
) -> PersistenceResult<()> {
    let key = storage_node_key(address_hash, path);
    batch.put(key.column, key.as_slice(), rlp)?;
    Ok(())
}

/// `BaseTriePersistence.WriteBatch.SelfDestruct`: deletes the storage trie of the account. Not strictly needed, as
/// traversal skips an orphaned trie.
pub(crate) fn self_destruct<R: KvReader>(
    snapshot: &R,
    batch: &mut impl ColumnSink,
    account_path: &Hash256,
) -> PersistenceResult<()> {
    let address_suffix = &account_path[STORAGE_PREFIX_PORTION..STORAGE_HASH_PREFIX_LENGTH];
    let mut first_key = [0u8; 1 + STORAGE_PREFIX_PORTION];
    let mut last_key = [0u8; FULL_STORAGE_NODES_KEY_LENGTH + 1];

    let short_last = SHORTENED_STORAGE_NODES_KEY_LENGTH + 1;
    create_storage_range(
        account_path,
        &mut first_key[..STORAGE_PREFIX_PORTION],
        &mut last_key[..short_last],
        STORAGE_PREFIX_PORTION,
    );
    delete_matching_keys_by_suffix(
        snapshot,
        Column::StorageNodes,
        batch,
        &first_key[..STORAGE_PREFIX_PORTION],
        &last_key[..short_last],
        STORAGE_PREFIX_PORTION + SHORTENED_PATH_LENGTH,
        address_suffix,
    )?;

    first_key[0] = 1;
    last_key[0] = 1;
    create_storage_range(
        account_path,
        &mut first_key[1..],
        &mut last_key[1..],
        STORAGE_PREFIX_PORTION,
    );
    delete_matching_keys_by_suffix(
        snapshot,
        Column::FallbackNodes,
        batch,
        &first_key,
        &last_key,
        1 + STORAGE_PREFIX_PORTION + FULL_PATH_LENGTH + PATH_LENGTH_LENGTH,
        address_suffix,
    )
}

/// How [`delete_contained_keys`] reads a node's path out of its key.
struct NodeKeyShape<'a> {
    column: Column,
    key_length: usize,
    path_offset: usize,
    path_bytes: usize,
    /// Whether the length is in the low nibble of the last path byte rather than in the byte after the path.
    packed_length: bool,
    /// The address bytes a storage node key must end with, from the given offset.
    address_suffix: Option<(usize, &'a [u8])>,
}

/// `BaseTriePersistence.WriteBatch.DeleteStateTrieNodeRange`: scans each state column over the range and deletes the
/// nodes whose whole subtree `[ToLowerBoundPath, ToUpperBoundPath]` lies in `[from, to]`.
pub(crate) fn delete_state_trie_node_range<R: KvReader>(
    snapshot: &R,
    batch: &mut impl ColumnSink,
    from: &Hash256,
    to: &Hash256,
) -> PersistenceResult<()> {
    let mut first_key = [0u8; FULL_STATE_NODES_KEY_LENGTH];
    let mut last_key = [0u8; FULL_STATE_NODES_KEY_LENGTH + 1];
    let from_path = TreePath::new(*from, 0);

    let top = STATE_NODES_TOP_PATH_LENGTH;
    encode_state_top_node_key(&mut first_key[..top], &from_path);
    encode_state_top_node_key(&mut last_key[..top], &TreePath::new(*to, STATE_NODES_TOP_THRESHOLD));
    last_key[top] = 0;
    let shape = NodeKeyShape {
        column: Column::StateTopNodes,
        key_length: top,
        path_offset: 0,
        path_bytes: top,
        packed_length: true,
        address_suffix: None,
    };
    delete_contained_keys(snapshot, batch, &first_key[..top], &last_key[..=top], &shape, from, to)?;

    let short = SHORTENED_PATH_LENGTH;
    encode_shortened_state_node_key(&mut first_key[..short], &from_path);
    encode_shortened_state_node_key(&mut last_key[..short], &TreePath::new(*to, SHORTENED_PATH_THRESHOLD));
    last_key[short] = 0;
    let shape = NodeKeyShape {
        column: Column::StateNodes,
        key_length: short,
        path_offset: 0,
        path_bytes: short,
        packed_length: true,
        address_suffix: None,
    };
    delete_contained_keys(
        snapshot,
        batch,
        &first_key[..short],
        &last_key[..=short],
        &shape,
        from,
        to,
    )?;

    let full = FULL_STATE_NODES_KEY_LENGTH;
    encode_full_state_node_key(&mut first_key, &from_path);
    encode_full_state_node_key(&mut last_key[..full], &TreePath::new(*to, TreePath::MAX_LENGTH));
    last_key[full] = 0;
    let shape = NodeKeyShape {
        column: Column::FallbackNodes,
        key_length: full,
        path_offset: 1,
        path_bytes: FULL_PATH_LENGTH,
        packed_length: false,
        address_suffix: None,
    };
    delete_contained_keys(snapshot, batch, &first_key, &last_key, &shape, from, to)
}

/// `BaseTriePersistence.WriteBatch.DeleteStorageTrieNodeRange`: as [`delete_state_trie_node_range`], in the storage
/// trie of `address_hash`.
pub(crate) fn delete_storage_trie_node_range<R: KvReader>(
    snapshot: &R,
    batch: &mut impl ColumnSink,
    address_hash: &Hash256,
    from: &Hash256,
    to: &Hash256,
) -> PersistenceResult<()> {
    let address_suffix = &address_hash[STORAGE_PREFIX_PORTION..STORAGE_HASH_PREFIX_LENGTH];
    let mut first_key = [0u8; FULL_STORAGE_NODES_KEY_LENGTH];
    let mut last_key = [0u8; FULL_STORAGE_NODES_KEY_LENGTH + 1];
    let from_path = TreePath::new(*from, 0);

    let short = SHORTENED_STORAGE_NODES_KEY_LENGTH;
    encode_shortened_storage_node_key(&mut first_key[..short], address_hash, &from_path);
    encode_shortened_storage_node_key(
        &mut last_key[..short],
        address_hash,
        &TreePath::new(*to, SHORTENED_PATH_THRESHOLD),
    );
    last_key[short] = 0;
    let shape = NodeKeyShape {
        column: Column::StorageNodes,
        key_length: short,
        path_offset: STORAGE_PREFIX_PORTION,
        path_bytes: SHORTENED_PATH_LENGTH,
        packed_length: true,
        address_suffix: Some((STORAGE_PREFIX_PORTION + SHORTENED_PATH_LENGTH, address_suffix)),
    };
    delete_contained_keys(
        snapshot,
        batch,
        &first_key[..short],
        &last_key[..=short],
        &shape,
        from,
        to,
    )?;

    let full = FULL_STORAGE_NODES_KEY_LENGTH;
    encode_full_storage_node_key(&mut first_key, address_hash, &from_path);
    encode_full_storage_node_key(
        &mut last_key[..full],
        address_hash,
        &TreePath::new(*to, TreePath::MAX_LENGTH),
    );
    last_key[full] = 0;
    let shape = NodeKeyShape {
        column: Column::FallbackNodes,
        key_length: full,
        path_offset: 1 + STORAGE_PREFIX_PORTION,
        path_bytes: FULL_PATH_LENGTH,
        packed_length: false,
        address_suffix: Some((
            1 + STORAGE_PREFIX_PORTION + FULL_PATH_LENGTH + PATH_LENGTH_LENGTH,
            address_suffix,
        )),
    };
    delete_contained_keys(snapshot, batch, &first_key, &last_key, &shape, from, to)
}

/// `BaseTriePersistence.DeleteContainedKeys`: scans `[first_key, last_key)` and deletes every node key whose node is
/// entirely contained in `[from, to]`.
fn delete_contained_keys<R: KvReader>(
    snapshot: &R,
    batch: &mut impl ColumnSink,
    first_key: &[u8],
    last_key: &[u8],
    shape: &NodeKeyShape<'_>,
    from: &Hash256,
    to: &Hash256,
) -> PersistenceResult<()> {
    for_each_between(snapshot, shape.column, first_key, last_key, |key, _| {
        if key.len() != shape.key_length {
            return Ok(());
        }
        if let Some((offset, suffix)) = shape.address_suffix
            && &key[offset..] != suffix
        {
            return Ok(());
        }

        let mut value = [0u8; FULL_PATH_LENGTH];
        value[..shape.path_bytes].copy_from_slice(&key[shape.path_offset..shape.path_offset + shape.path_bytes]);
        let length = if shape.packed_length {
            let last = shape.path_bytes - 1;
            let length = value[last] & 0x0F;
            // The low nibble held the length, not path data.
            value[last] &= 0xF0;
            length
        } else {
            key[shape.path_offset + shape.path_bytes]
        };

        if is_fully_contained(&value, length, from, to)? {
            batch.remove(shape.column, key)?;
        }
        Ok(())
    })
}

/// `BaseTriePersistence.IsFullyContained`: whether the subtree of the node at `(value, length)` lies in `[from, to]`.
fn is_fully_contained(value: &Hash256, length: u8, from: &Hash256, to: &Hash256) -> PersistenceResult<bool> {
    if length > TreePath::MAX_LENGTH {
        return Err(PersistenceError::InvalidOperation(
            "TreePath can't represent more than 64 nibble.".to_owned(),
        ));
    }

    let path = TreePath::new(*value, length);
    Ok(*from <= path.to_lower_bound_path() && path.to_upper_bound_path() <= *to)
}
