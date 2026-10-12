//! Port of `Nethermind.State.Flat/Persistence/BasePersistence.cs`: the metadata column (state pointer, format
//! markers, wipe marker), slot encoding resolution, the wipe, and the key-range scans the write batches delete with.

use crate::codec::metadata::{
    CURRENT_STATE_KEY, LAYOUT_KEY, SLOT_ENCODING_KEY, SLOT_ENCODING_RAW, SLOT_ENCODING_RLP, WIPED_FOR_SYNC_KEY,
    decode_state_id, encode_state_id,
};
use crate::kv::{
    Column, IterOptions, KvIterator, KvRawBatch, KvReader, KvResult, KvStore, ReadFlags, WriteBatch, WriteFlags,
};
use crate::types::{FlatLayout, StateId};

use super::{PersistenceError, PersistenceResult};

/// `BasePersistence.StoragePrefixPortion`.
pub const STORAGE_PREFIX_PORTION: usize = crate::codec::keys::STORAGE_PREFIX_PORTION;

/// Keys deleted per batch by [`clear_all_columns`]; one batch over every key exhausts memory on a large DB (#11442).
pub const CLEAR_BATCH_SIZE: usize = 10_000;

const RAW_SLOT_DEPRECATION_MESSAGE: &str = "Flat DB uses the legacy raw storage slot encoding, which is deprecated and will be removed in a future release. Please resync to adopt the RLP slot encoding.";

/// A column read with `IReadOnlyKeyValueStore.Get(key)`, live or at a snapshot.
pub trait ColumnRead {
    fn get_value(&self, key: &[u8]) -> KvResult<Option<Vec<u8>>>;
}

/// A column of the live store.
pub struct LiveColumn<'a, K>(pub &'a K, pub Column);

impl<K: KvStore> ColumnRead for LiveColumn<'_, K> {
    fn get_value(&self, key: &[u8]) -> KvResult<Option<Vec<u8>>> {
        self.0.get_with(self.1, key, <[u8]>::to_vec)
    }
}

/// A column of a reader's snapshot.
pub struct SnapshotColumn<'a, R>(pub &'a R, pub Column);

impl<R: KvReader> ColumnRead for SnapshotColumn<'_, R> {
    fn get_value(&self, key: &[u8]) -> KvResult<Option<Vec<u8>>> {
        self.0.get_with(self.1, key, ReadFlags::NONE, <[u8]>::to_vec)
    }
}

/// A column written with `IWriteOnlyKeyValueStore.PutSpan(key, value)` and `Remove(key)`: default write flags.
pub trait ColumnWrite {
    fn put(&mut self, key: &[u8], value: &[u8]) -> KvResult<()>;
    fn remove(&mut self, key: &[u8]) -> KvResult<()>;
}

/// A column of a write batch, as `IColumnsWriteBatch.GetColumnBatch` returns it.
pub struct BatchColumn<'a, B: KvRawBatch>(pub &'a mut WriteBatch<B>, pub Column);

impl<B: KvRawBatch> ColumnWrite for BatchColumn<'_, B> {
    fn put(&mut self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.0.put(self.1, key, value, WriteFlags::NONE)
    }

    fn remove(&mut self, key: &[u8]) -> KvResult<()> {
        self.0.delete(self.1, key, WriteFlags::NONE)
    }
}

/// The per-column writes of a persistence write batch, as the column batches of `IColumnsWriteBatch` take them.
pub trait ColumnSink {
    /// `PutSpan(key, value, flags)` with the batch's write flags.
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) -> KvResult<()>;

    /// `Remove(key)`, which writes with default flags.
    fn remove(&mut self, column: Column, key: &[u8]) -> KvResult<()>;
}

/// `BasePersistence.ReadCurrentState`: the persisted state pointer, or the pre-genesis state when there is none.
pub fn read_current_state(metadata: &impl ColumnRead) -> PersistenceResult<StateId> {
    let bytes = metadata.get_value(&*CURRENT_STATE_KEY)?;
    decode_state_id(bytes.as_deref()).ok_or_else(|| {
        PersistenceError::InvalidConfiguration(format!(
            "Flat DB metadata holds a {}-byte state pointer; the DB may be corrupt.",
            bytes.map_or(0, |bytes| bytes.len())
        ))
    })
}

/// `BasePersistence.SetCurrentState`. A persisted state pointer means the sync that followed a wipe has completed,
/// so it also drops the wipe marker.
pub fn set_current_state(metadata: &mut impl ColumnWrite, state: &StateId) -> PersistenceResult<()> {
    metadata.put(&*CURRENT_STATE_KEY, &encode_state_id(state))?;
    metadata.remove(&*WIPED_FOR_SYNC_KEY)?;
    Ok(())
}

/// `BasePersistence.ReadLayout`.
pub fn read_layout(metadata: &impl ColumnRead) -> PersistenceResult<Option<FlatLayout>> {
    let Some(bytes) = metadata.get_value(&*LAYOUT_KEY)? else {
        return Ok(None);
    };
    let Some(&byte) = bytes.first() else {
        return Ok(None);
    };

    FlatLayout::from_u8(byte).map(Some).ok_or_else(|| {
        PersistenceError::InvalidConfiguration(format!(
            "Flat DB metadata contains an unrecognized layout byte '{byte}'. The DB may be corrupt or was written by a newer version."
        ))
    })
}

/// `BasePersistence.SetLayout`: records the layout and the RLP slot encoding. Only for DBs on the current format;
/// legacy raw DBs are never re-stamped.
pub fn set_layout(metadata: &mut impl ColumnWrite, layout: FlatLayout) -> PersistenceResult<()> {
    metadata.put(&*LAYOUT_KEY, &[layout as u8])?;
    set_slot_encoding(metadata, SLOT_ENCODING_RLP)
}

/// `BasePersistence.ValidateLayout`: fails when the DB was synced with another layout; a fresh DB passes, and its
/// layout is recorded by the first batch.
pub fn validate_layout<K: KvStore>(kv: &K, layout: FlatLayout) -> PersistenceResult<()> {
    match read_layout(&LiveColumn(kv, Column::Metadata))? {
        Some(stored) if stored != layout => Err(PersistenceError::InvalidConfiguration(format!(
            "Flat DB was previously synced with layout '{stored}', but the configured layout is '{layout}'. Either set 'IFlatDbConfig.Layout' back to '{stored}', or wipe the flat DB and re-sync."
        ))),
        _ => Ok(()),
    }
}

/// `BasePersistence.ReadSlotEncoding`.
pub fn read_slot_encoding(metadata: &impl ColumnRead) -> PersistenceResult<Option<u8>> {
    Ok(metadata
        .get_value(&*SLOT_ENCODING_KEY)?
        .and_then(|bytes| bytes.first().copied()))
}

/// `BasePersistence.SetSlotEncoding`.
pub fn set_slot_encoding(metadata: &mut impl ColumnWrite, version: u8) -> PersistenceResult<()> {
    metadata.put(&*SLOT_ENCODING_KEY, &[version])?;
    Ok(())
}

/// `BasePersistence.ResolveSlotEncoding`: whether slot values are RLP-wrapped. The recorded version wins; without
/// one the DB is ambiguous, as a brand-new DB and a pre-feature DB both lack it, so a DB with slots in `slot_column`
/// is legacy raw (with a deprecation warning) and an empty one wraps. The layout marker is no discriminator: it
/// postdates flat sync and tooling can bypass the metadata column.
pub fn resolve_slot_encoding<K: KvStore>(kv: &K, slot_column: Column) -> PersistenceResult<bool> {
    let rlp_wrap = match read_slot_encoding(&LiveColumn(kv, Column::Metadata))? {
        Some(SLOT_ENCODING_RLP) => true,
        Some(SLOT_ENCODING_RAW) => false,
        None => first_key(kv, slot_column)?.is_none(),
        Some(version) => {
            return Err(PersistenceError::InvalidConfiguration(format!(
                "Flat DB metadata contains an unrecognized slot encoding version '{version}'. The DB may be corrupt or was written by a newer version."
            )));
        }
    };

    if !rlp_wrap {
        log::warn!("{RAW_SLOT_DEPRECATION_MESSAGE}");
    }
    Ok(rlp_wrap)
}

/// `ISortedKeyValueStore.FirstKey` of a column of the live store.
pub fn first_key<K: KvStore>(kv: &K, column: Column) -> PersistenceResult<Option<Vec<u8>>> {
    let reader = kv.reader(false)?;
    let mut iter = reader.iter(column, IterOptions::default())?;
    iter.seek_to_first();
    let key = iter.valid().then(|| iter.key().to_vec());
    iter.status()?;
    Ok(key)
}

/// `BasePersistence.ReadWipedForSync`: whether [`clear_all_columns`] marked the DB as wiped for a state sync. Set by
/// the first batch of a wipe and cleared when a state pointer is persisted: with a pre-genesis pointer it tells a DB
/// awaiting its sync from an unused one; with a pointer still present it means the wipe itself was interrupted.
pub fn read_wiped_for_sync(metadata: &impl ColumnRead) -> PersistenceResult<bool> {
    Ok(metadata
        .get_value(&*WIPED_FOR_SYNC_KEY)?
        .is_some_and(|bytes| !bytes.is_empty()))
}

/// `BasePersistence.ClearAllColumns`: wipes every data column and the state pointer in batches of
/// [`CLEAR_BATCH_SIZE`] deletes, keeping the format markers (wiping them makes a re-synced RLP DB read back as raw,
/// #11996). The wipe marker is the first write and the pointer reset the last, so a wipe that dies midway reads back
/// as "pointer and marker both present" and the next start redoes it.
pub fn clear_all_columns<K: KvStore>(kv: &K) -> PersistenceResult<()> {
    let mut batch = kv.write_batch()?;
    batch.put(Column::Metadata, &*WIPED_FOR_SYNC_KEY, &[1], WriteFlags::NONE)?;

    let mut count = 0;
    for column in Column::ALL.into_iter().filter(|&column| column != Column::Metadata) {
        let reader = kv.reader(false)?;
        let mut iter = reader.iter(column, IterOptions::default())?;
        iter.seek_to_first();
        while iter.valid() {
            batch.delete(column, iter.key(), WriteFlags::NONE)?;
            count += 1;
            if count == CLEAR_BATCH_SIZE {
                let next = kv.write_batch()?;
                std::mem::replace(&mut batch, next).commit()?;
                count = 0;
            }
            iter.next();
        }
        iter.status()?;
    }

    batch.delete(Column::Metadata, &*CURRENT_STATE_KEY, WriteFlags::NONE)?;
    batch.commit()?;
    Ok(())
}

/// `BasePersistence.CreateStorageRange`: the bounds of every key leading with `account_path[..prefix_portion]`.
pub(crate) fn create_storage_range(
    account_path: &[u8],
    first_key: &mut [u8],
    last_key: &mut [u8],
    prefix_portion: usize,
) {
    first_key[..prefix_portion].copy_from_slice(&account_path[..prefix_portion]);
    last_key[..prefix_portion].copy_from_slice(&account_path[..prefix_portion]);
    first_key[prefix_portion..].fill(0);
    last_key[prefix_portion..].fill(0xFF);
}

/// Calls `visit` with every entry of `column` in `[first_key, last_key)` at the snapshot of `reader`, as a walk over
/// `ISortedKeyValueStore.GetViewBetween` does.
pub(crate) fn for_each_between<R: KvReader>(
    reader: &R,
    column: Column,
    first_key: &[u8],
    last_key: &[u8],
    mut visit: impl FnMut(&[u8], &[u8]) -> PersistenceResult<()>,
) -> PersistenceResult<()> {
    let options = IterOptions {
        lower_bound: Some(first_key),
        upper_bound: Some(last_key),
        flags: ReadFlags::NONE,
    };
    let mut iter = reader.iter(column, options)?;
    iter.seek_to_first();
    while iter.valid() {
        visit(iter.key(), iter.value())?;
        iter.next();
    }
    iter.status()?;
    Ok(())
}

/// `BasePersistence.DeleteMatchingKeys` by length: deletes the keys of `[first_key, last_key)` that are
/// `expected_key_length` long.
pub(crate) fn delete_matching_keys_by_length<R: KvReader>(
    snapshot: &R,
    column: Column,
    batch: &mut impl ColumnSink,
    first_key: &[u8],
    last_key: &[u8],
    expected_key_length: usize,
) -> PersistenceResult<()> {
    for_each_between(snapshot, column, first_key, last_key, |key, _| {
        if key.len() == expected_key_length {
            batch.remove(column, key)?;
        }
        Ok(())
    })
}

/// `BasePersistence.DeleteMatchingKeys` by suffix: deletes the keys of `[first_key, last_key)` that end, from
/// `suffix_offset`, with exactly `expected_suffix`.
pub(crate) fn delete_matching_keys_by_suffix<R: KvReader>(
    snapshot: &R,
    column: Column,
    batch: &mut impl ColumnSink,
    first_key: &[u8],
    last_key: &[u8],
    suffix_offset: usize,
    expected_suffix: &[u8],
) -> PersistenceResult<()> {
    for_each_between(snapshot, column, first_key, last_key, |key, _| {
        if key.len() >= suffix_offset + expected_suffix.len() && &key[suffix_offset..] == expected_suffix {
            batch.remove(column, key)?;
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::{
        BatchColumn, LiveColumn, clear_all_columns, read_current_state, read_layout, read_slot_encoding,
        read_wiped_for_sync, set_current_state, set_layout, validate_layout,
    };
    use crate::codec::metadata::{SLOT_ENCODING_KEY, SLOT_ENCODING_RLP};
    use crate::kv::mem::MemKv;
    use crate::kv::{Column, IterOptions, KvIterator, KvReader, KvStore, WriteFlags};
    use crate::persistence::PersistenceError;
    use crate::types::{FlatLayout, StateId};
    use rstest::rstest;

    fn with_metadata(kv: &MemKv, write: impl FnOnce(&mut BatchColumn<'_, crate::kv::mem::MemBatch>)) {
        let mut batch = kv.write_batch().unwrap();
        write(&mut BatchColumn(&mut batch, Column::Metadata));
        batch.commit().unwrap();
    }

    fn keys(kv: &MemKv, column: Column) -> usize {
        let reader = kv.reader(false).unwrap();
        let mut iter = reader.iter(column, IterOptions::default()).unwrap();
        let mut count = 0;
        iter.seek_to_first();
        while iter.valid() {
            count += 1;
            iter.next();
        }
        count
    }

    /// Port of `LayoutMetadataTests`: round trips, the RLP marker, validation of every ordered pair of layouts.
    #[rstest]
    fn layout_markers_round_trip_and_validate(
        #[values(
            FlatLayout::Flat,
            FlatLayout::FlatInTrie,
            FlatLayout::PreimageFlatV1,
            FlatLayout::PreimageFlat
        )]
        stored: FlatLayout,
    ) {
        let kv = MemKv::new();
        let metadata = LiveColumn(&kv, Column::Metadata);
        assert_eq!(read_layout(&metadata).unwrap(), None);
        for layout in FlatLayout::ALL {
            assert!(validate_layout(&kv, layout).is_ok(), "a fresh DB accepts any layout");
        }

        with_metadata(&kv, |metadata| set_layout(metadata, stored).unwrap());

        assert_eq!(read_layout(&metadata).unwrap(), Some(stored));
        assert_eq!(read_slot_encoding(&metadata).unwrap(), Some(SLOT_ENCODING_RLP));
        assert_eq!(
            kv.get_with(Column::Metadata, &*SLOT_ENCODING_KEY, <[u8]>::to_vec)
                .unwrap(),
            Some(vec![SLOT_ENCODING_RLP])
        );
        for configured in FlatLayout::ALL {
            match validate_layout(&kv, configured) {
                Ok(()) => assert_eq!(configured, stored),
                Err(PersistenceError::InvalidConfiguration(message)) => {
                    assert_ne!(configured, stored);
                    assert!(message.contains(&stored.to_string()) && message.contains(&configured.to_string()));
                }
                Err(error) => panic!("unexpected {error:?}"),
            }
        }
    }

    #[test]
    fn unknown_layout_byte_is_rejected() {
        let kv = MemKv::new();
        with_metadata(&kv, |metadata| {
            use super::ColumnWrite;
            metadata.put(&*crate::codec::metadata::LAYOUT_KEY, &[9]).unwrap();
        });
        assert!(matches!(
            read_layout(&LiveColumn(&kv, Column::Metadata)),
            Err(PersistenceError::InvalidConfiguration(_))
        ));
    }

    /// Port of `ClearColumnsTests.ClearAllColumns_PreservesFormatMarkers_ResetsStateMetadata_AndWipesData` and
    /// `ClearAllColumnsBatchingTests`: the wipe spans several batches, keeps the format markers, resets only the
    /// state pointer and marks the DB as wiped; the marker goes first and the pointer reset last.
    #[test]
    fn clear_all_columns_wipes_data_in_batches_and_keeps_format_markers() {
        let kv = MemKv::new();
        let metadata = LiveColumn(&kv, Column::Metadata);
        with_metadata(&kv, |metadata| {
            set_layout(metadata, FlatLayout::Flat).unwrap();
            set_current_state(metadata, &StateId::new(123, [0x11; 32])).unwrap();
        });
        let mut batch = kv.write_batch().unwrap();
        for i in 0..25_000u32 {
            let column = if i % 2 == 0 { Column::Storage } else { Column::Account };
            batch.put(column, &i.to_be_bytes(), &[1], WriteFlags::NONE).unwrap();
        }
        batch.commit().unwrap();
        assert!(!read_wiped_for_sync(&metadata).unwrap());
        let writes_before = kv.writes().len();

        clear_all_columns(&kv).unwrap();

        let writes = &kv.writes()[writes_before..];
        assert_eq!(keys(&kv, Column::Storage) + keys(&kv, Column::Account), 0);
        assert_eq!(read_layout(&metadata).unwrap(), Some(FlatLayout::Flat));
        assert_eq!(read_slot_encoding(&metadata).unwrap(), Some(SLOT_ENCODING_RLP));
        assert_eq!(read_current_state(&metadata).unwrap(), StateId::PRE_GENESIS);
        assert!(read_wiped_for_sync(&metadata).unwrap());
        // The marker and 10 000 deletes, then 10 000 deletes, then the last 5 000 and the pointer reset.
        assert_eq!(
            writes.iter().map(|write| write.operations).collect::<Vec<_>>(),
            [10_001, 10_000, 5_001]
        );
    }
}
