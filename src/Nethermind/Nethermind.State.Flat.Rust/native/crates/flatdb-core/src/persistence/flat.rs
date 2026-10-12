//! Port of `Nethermind.State.Flat/Persistence/BaseFlatPersistence.cs`: accounts and slots in two columns, keyed by
//! address (hash) and slot (hash) as [`crate::codec::keys`] lays them out.

use crate::codec::keys::{
    ACCOUNT_KEY_LENGTH, STORAGE_KEY_LENGTH, STORAGE_SLOT_KEY_SIZE, account_key, storage_address_suffix, storage_key,
    storage_prefix_length,
};
use crate::codec::rlp::decode_bytes;
use crate::codec::slot::{RLP_SLOT_VALUE_BUFFER_SIZE, STORAGE_VALUE_SIZE, encode_slot_value};
use crate::kv::{Column, GetInto, IterOptions, KvIterator, KvReader, ReadFlags};
use crate::types::{Hash256, U256};

use super::base::{ColumnSink, create_storage_range, delete_matching_keys_by_length, delete_matching_keys_by_suffix};
use super::{FlatIterator, PersistenceError, PersistenceResult};

/// Size of the buffer an account is read into (`ToHashedFlatReader._accountSpanBufferSize`).
pub const ACCOUNT_BUFFER_SIZE: usize = 256;

/// Where and how a layout keeps flat state.
#[derive(Clone, Copy, Debug)]
pub struct FlatShape {
    pub account_column: Column,
    pub storage_column: Column,
    /// Whether slot values are RLP-wrapped rather than legacy raw.
    pub rlp_wrap_slots: bool,
    /// Whether storage keys lead with the whole address instead of splitting it around the slot.
    pub full_address_storage_key: bool,
}

/// `IReadOnlyKeyValueStore.Get(key, Span)`: the number of bytes copied into `buffer`, 0 when the key is absent (or its
/// value empty). A value that does not fit fails, as the C# bindings throw.
pub(crate) fn span_get<R: KvReader>(
    reader: &R,
    column: Column,
    key: &[u8],
    buffer: &mut [u8],
) -> PersistenceResult<usize> {
    match reader.get(column, key, ReadFlags::NONE, buffer)? {
        GetInto::NotFound => Ok(0),
        GetInto::Found(len) => Ok(len),
        GetInto::BufferTooSmall(len) => Err(PersistenceError::ValueTooLarge {
            len,
            capacity: buffer.len(),
        }),
    }
}

/// `BaseFlatPersistence.ThrowSlotValueTooLong`.
pub(crate) fn slot_value_too_long(length: usize, rlp_wrap_slots: bool) -> PersistenceError {
    PersistenceError::InvalidConfiguration(format!(
        "Flat DB storage slot value is {length} bytes, exceeding the {STORAGE_VALUE_SIZE}-byte maximum (rlpWrapSlots={}). The slot-encoding metadata is likely missing or mismatched (RLP-wrapped values read as raw). Re-sync the flat DB to recover.",
        if rlp_wrap_slots { "True" } else { "False" }
    ))
}

/// The stripped slot value within a stored one.
fn decode_stored_slot(value: &[u8], rlp_wrap_slots: bool) -> PersistenceResult<&[u8]> {
    let value = if rlp_wrap_slots {
        decode_bytes(value, 0)?.0
    } else {
        value
    };
    if value.len() > STORAGE_VALUE_SIZE {
        return Err(slot_value_too_long(value.len(), rlp_wrap_slots));
    }
    Ok(value)
}

/// `BaseFlatPersistence.Reader.GetAccount`: calls `read` with the stored account value, read into a
/// [`ACCOUNT_BUFFER_SIZE`] buffer.
pub(crate) fn read_account<R: KvReader, T>(
    reader: &R,
    shape: &FlatShape,
    address_hash: &Hash256,
    read: impl FnOnce(&[u8]) -> T,
) -> PersistenceResult<Option<T>> {
    let mut buffer = [0u8; ACCOUNT_BUFFER_SIZE];
    let size = span_get(reader, shape.account_column, &account_key(address_hash), &mut buffer)?;
    Ok((size != 0).then(|| read(&buffer[..size])))
}

/// `BaseFlatPersistence.Reader.TryGetStorage`.
pub(crate) fn try_get_storage<R: KvReader>(
    reader: &R,
    shape: &FlatShape,
    address_hash: &Hash256,
    slot_hash: &Hash256,
) -> PersistenceResult<Option<U256>> {
    let key = storage_key(address_hash, slot_hash, shape.full_address_storage_key);
    let mut buffer = [0u8; RLP_SLOT_VALUE_BUFFER_SIZE];
    let size = span_get(reader, shape.storage_column, &key, &mut buffer)?;
    if size == 0 {
        return Ok(None);
    }

    let value = decode_stored_slot(&buffer[..size], shape.rlp_wrap_slots)?;
    Ok(Some(U256::from_be_slice(value)))
}

/// `BaseFlatPersistence.Reader.CreateAccountIterator`: the accounts in `[start_key[..20], end_key[..20])`.
pub(crate) fn create_account_iterator<R: KvReader>(
    reader: &R,
    shape: &FlatShape,
    start_key: &Hash256,
    end_key: &Hash256,
) -> PersistenceResult<FlatKvIterator<R::Iter>> {
    let options = IterOptions {
        lower_bound: Some(&start_key[..ACCOUNT_KEY_LENGTH]),
        upper_bound: Some(&end_key[..ACCOUNT_KEY_LENGTH]),
        flags: ReadFlags::NONE,
    };
    Ok(FlatKvIterator::Account(AccountIterator {
        view: View::new(reader.iter(shape.account_column, options)?),
        current_key: [0; 32],
    }))
}

/// `BaseFlatPersistence.Reader.CreateStorageIterator`: the slots of `account_key` in `[start_slot_key, end_slot_key]`.
/// A split key range also holds the slots of accounts sharing the leading address bytes; their trailing bytes are
/// re-checked.
pub(crate) fn create_storage_iterator<R: KvReader>(
    reader: &R,
    shape: &FlatShape,
    account_key: &Hash256,
    start_slot_key: &Hash256,
    end_slot_key: &Hash256,
) -> PersistenceResult<FlatKvIterator<R::Iter>> {
    let full = shape.full_address_storage_key;
    let first_key = storage_key(account_key, start_slot_key, full);
    let mut last_key = [0u8; STORAGE_KEY_LENGTH + 1];
    last_key[..STORAGE_KEY_LENGTH].copy_from_slice(&storage_key(account_key, end_slot_key, full));

    let options = IterOptions {
        lower_bound: Some(&first_key),
        upper_bound: Some(&last_key),
        flags: ReadFlags::NONE,
    };
    let suffix = storage_address_suffix(account_key, full);
    let mut address_suffix = [0u8; 16];
    address_suffix[..suffix.len()].copy_from_slice(suffix);
    Ok(FlatKvIterator::Storage(StorageIterator {
        view: View::new(reader.iter(shape.storage_column, options)?),
        address_suffix,
        address_suffix_len: suffix.len(),
        slot_offset: storage_prefix_length(full),
        rlp_wrap_slots: shape.rlp_wrap_slots,
        current_key: [0; 32],
        value_start: 0,
        value_len: 0,
    }))
}

/// `RocksdbSortedView`: the first move seeks to the first entry, the others step forward.
struct View<I> {
    iter: I,
    started: bool,
}

impl<I: KvIterator> View<I> {
    fn new(iter: I) -> Self {
        Self { iter, started: false }
    }

    fn move_next(&mut self) -> PersistenceResult<bool> {
        if !self.started {
            self.iter.seek_to_first();
            self.started = true;
        } else if self.iter.valid() {
            self.iter.next();
        }

        if self.iter.valid() {
            return Ok(true);
        }
        self.iter.status()?;
        Ok(false)
    }
}

/// `BaseFlatPersistence.AccountIterator`: skips keys that are not account keys (in `FlatInTrie`, the account column
/// also holds state trie nodes).
pub struct AccountIterator<I> {
    view: View<I>,
    current_key: Hash256,
}

impl<I: KvIterator + 'static> FlatIterator for AccountIterator<I> {
    fn move_next(&mut self) -> PersistenceResult<bool> {
        while self.view.move_next()? {
            let key = self.view.iter.key();
            if key.len() != ACCOUNT_KEY_LENGTH {
                continue;
            }

            self.current_key = [0; 32];
            self.current_key[..ACCOUNT_KEY_LENGTH].copy_from_slice(key);
            return Ok(true);
        }
        Ok(false)
    }

    fn current_key(&self) -> Hash256 {
        self.current_key
    }

    fn current_value(&self) -> &[u8] {
        self.view.iter.value()
    }
}

/// `BaseFlatPersistence.StorageIterator`: yields the slot hash and the stripped value of each slot of one account.
pub struct StorageIterator<I> {
    view: View<I>,
    address_suffix: [u8; 16],
    address_suffix_len: usize,
    slot_offset: usize,
    rlp_wrap_slots: bool,
    current_key: Hash256,
    value_start: usize,
    value_len: usize,
}

impl<I: KvIterator + 'static> FlatIterator for StorageIterator<I> {
    fn move_next(&mut self) -> PersistenceResult<bool> {
        while self.view.move_next()? {
            let key = self.view.iter.key();
            if key.len() != STORAGE_KEY_LENGTH
                || key[self.slot_offset + STORAGE_SLOT_KEY_SIZE..] != self.address_suffix[..self.address_suffix_len]
            {
                continue;
            }

            self.current_key = key[self.slot_offset..self.slot_offset + STORAGE_SLOT_KEY_SIZE]
                .try_into()
                .expect("32 bytes");
            // A slot value over 32 bytes means the encoding is mismatched; failing here keeps snap-sync healing
            // from building wrong trie nodes out of it, as `TryGetStorage` does for point reads.
            let stored = self.view.iter.value();
            let value = decode_stored_slot(stored, self.rlp_wrap_slots)?;
            self.value_start = value.as_ptr().addr() - stored.as_ptr().addr();
            self.value_len = value.len();
            return Ok(true);
        }
        Ok(false)
    }

    fn current_key(&self) -> Hash256 {
        self.current_key
    }

    fn current_value(&self) -> &[u8] {
        &self.view.iter.value()[self.value_start..self.value_start + self.value_len]
    }
}

/// The iterators of the flat columns.
pub enum FlatKvIterator<I> {
    Account(AccountIterator<I>),
    Storage(StorageIterator<I>),
}

impl<I: KvIterator + 'static> FlatIterator for FlatKvIterator<I> {
    fn move_next(&mut self) -> PersistenceResult<bool> {
        match self {
            Self::Account(iter) => iter.move_next(),
            Self::Storage(iter) => iter.move_next(),
        }
    }

    fn current_key(&self) -> Hash256 {
        match self {
            Self::Account(iter) => iter.current_key(),
            Self::Storage(iter) => iter.current_key(),
        }
    }

    fn current_value(&self) -> &[u8] {
        match self {
            Self::Account(iter) => iter.current_value(),
            Self::Storage(iter) => iter.current_value(),
        }
    }
}

/// `BaseFlatPersistence.WriteBatch.SelfDestruct`: deletes every slot of the account.
pub(crate) fn self_destruct<R: KvReader>(
    snapshot: &R,
    shape: &FlatShape,
    batch: &mut impl ColumnSink,
    account_path: &Hash256,
) -> PersistenceResult<()> {
    let prefix_length = storage_prefix_length(shape.full_address_storage_key);
    let mut first_key = [0u8; ACCOUNT_KEY_LENGTH];
    let mut last_key = [0u8; STORAGE_KEY_LENGTH + 1];
    create_storage_range(
        account_path,
        &mut first_key[..prefix_length],
        &mut last_key,
        prefix_length,
    );
    delete_matching_keys_by_suffix(
        snapshot,
        shape.storage_column,
        batch,
        &first_key[..prefix_length],
        &last_key,
        prefix_length + STORAGE_SLOT_KEY_SIZE,
        storage_address_suffix(account_path, shape.full_address_storage_key),
    )
}

/// `BaseFlatPersistence.WriteBatch.RemoveAccount`.
pub(crate) fn remove_account(
    batch: &mut impl ColumnSink,
    shape: &FlatShape,
    address_hash: &Hash256,
) -> PersistenceResult<()> {
    batch.remove(shape.account_column, &account_key(address_hash))?;
    Ok(())
}

/// `BaseFlatPersistence.WriteBatch.SetAccount`.
pub(crate) fn set_account(
    batch: &mut impl ColumnSink,
    shape: &FlatShape,
    address_hash: &Hash256,
    account: &[u8],
) -> PersistenceResult<()> {
    batch.put(shape.account_column, &account_key(address_hash), account)?;
    Ok(())
}

/// `BaseFlatPersistence.WriteBatch.SetStorage`: stores the encoded value, or deletes the slot for `None`.
pub(crate) fn set_storage(
    batch: &mut impl ColumnSink,
    shape: &FlatShape,
    address_hash: &Hash256,
    slot_hash: &Hash256,
    value: Option<&U256>,
) -> PersistenceResult<()> {
    let key = storage_key(address_hash, slot_hash, shape.full_address_storage_key);
    match value {
        Some(value) => {
            let mut buffer = [0u8; RLP_SLOT_VALUE_BUFFER_SIZE];
            let written = encode_slot_value(value, shape.rlp_wrap_slots, &mut buffer);
            batch.put(shape.storage_column, &key, &buffer[..written])?;
        }
        None => batch.remove(shape.storage_column, &key)?,
    }
    Ok(())
}

/// `BaseFlatPersistence.WriteBatch.SetStorageEncoded`: the trie leaf value `RLP(stripped)` is the stored form when
/// slots are wrapped, so it is stored verbatim, bounded to its one item; raw mode has no such shortcut.
pub(crate) fn set_storage_encoded(
    batch: &mut impl ColumnSink,
    shape: &FlatShape,
    address_hash: &Hash256,
    slot_hash: &Hash256,
    rlp_value: &[u8],
) -> PersistenceResult<()> {
    if !shape.rlp_wrap_slots {
        return Err(PersistenceError::NotSupported(
            "Encoded slot writes require RLP slot wrapping",
        ));
    }

    let key = storage_key(address_hash, slot_hash, shape.full_address_storage_key);
    let (_, end) = decode_bytes(rlp_value, 0)?;
    batch.put(shape.storage_column, &key, &rlp_value[..end])?;
    Ok(())
}

/// `BaseFlatPersistence.WriteBatch.DeleteAccountRange`: deletes the accounts in `[from[..20], to[..20]]`.
pub(crate) fn delete_account_range<R: KvReader>(
    snapshot: &R,
    shape: &FlatShape,
    batch: &mut impl ColumnSink,
    from_path: &Hash256,
    to_path: &Hash256,
) -> PersistenceResult<()> {
    let mut last_key = [0u8; ACCOUNT_KEY_LENGTH + 1];
    last_key[..ACCOUNT_KEY_LENGTH].copy_from_slice(&to_path[..ACCOUNT_KEY_LENGTH]);
    delete_matching_keys_by_length(
        snapshot,
        shape.account_column,
        batch,
        &from_path[..ACCOUNT_KEY_LENGTH],
        &last_key,
        ACCOUNT_KEY_LENGTH,
    )
}

/// `BaseFlatPersistence.WriteBatch.DeleteStorageRange`: deletes the slots of the account in `[from, to]`.
pub(crate) fn delete_storage_range<R: KvReader>(
    snapshot: &R,
    shape: &FlatShape,
    batch: &mut impl ColumnSink,
    address_hash: &Hash256,
    from_path: &Hash256,
    to_path: &Hash256,
) -> PersistenceResult<()> {
    let full = shape.full_address_storage_key;
    let first_key = storage_key(address_hash, from_path, full);
    let mut last_key = [0u8; STORAGE_KEY_LENGTH + 1];
    last_key[..STORAGE_KEY_LENGTH].copy_from_slice(&storage_key(address_hash, to_path, full));
    delete_matching_keys_by_suffix(
        snapshot,
        shape.storage_column,
        batch,
        &first_key,
        &last_key,
        storage_prefix_length(full) + STORAGE_SLOT_KEY_SIZE,
        storage_address_suffix(address_hash, full),
    )
}

#[cfg(test)]
mod tests {
    use super::{ColumnSink, FlatShape, create_storage_iterator, set_storage, try_get_storage};
    use crate::kv::mem::MemKv;
    use crate::kv::{
        Column, GetInto, IterOptions, KvIterator, KvReader, KvResult, KvStore, ReadFlags, WriteBatch, WriteFlags,
    };
    use crate::persistence::{FlatIterator, PersistenceError};
    use crate::types::U256;
    use rstest::rstest;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};

    /// A reader whose iterators count the entries they walk, including the ones a scan filters back out.
    struct CountingReader<R> {
        inner: R,
        walked: Arc<AtomicUsize>,
    }

    struct CountingIter<I> {
        inner: I,
        walked: Arc<AtomicUsize>,
    }

    impl<I: KvIterator> CountingIter<I> {
        fn count(&self) {
            if self.inner.valid() {
                self.walked.fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    impl<R: KvReader> KvReader for CountingReader<R> {
        type Iter = CountingIter<R::Iter>;

        fn get(&self, column: Column, key: &[u8], flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
            self.inner.get(column, key, flags, buffer)
        }

        fn get_with<T>(
            &self,
            column: Column,
            key: &[u8],
            flags: ReadFlags,
            read: impl FnOnce(&[u8]) -> T,
        ) -> KvResult<Option<T>> {
            self.inner.get_with(column, key, flags, read)
        }

        fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<Self::Iter> {
            Ok(CountingIter {
                inner: self.inner.iter(column, options)?,
                walked: self.walked.clone(),
            })
        }
    }

    impl<I: KvIterator> KvIterator for CountingIter<I> {
        fn seek(&mut self, key: &[u8]) {
            self.inner.seek(key);
            self.count();
        }

        fn seek_for_prev(&mut self, key: &[u8]) {
            self.inner.seek_for_prev(key);
            self.count();
        }

        fn seek_to_first(&mut self) {
            self.inner.seek_to_first();
            self.count();
        }

        fn seek_to_last(&mut self) {
            self.inner.seek_to_last();
            self.count();
        }

        fn valid(&self) -> bool {
            self.inner.valid()
        }

        fn next(&mut self) {
            self.inner.next();
            self.count();
        }

        fn prev(&mut self) {
            self.inner.prev();
            self.count();
        }

        fn key(&self) -> &[u8] {
            self.inner.key()
        }

        fn value(&self) -> &[u8] {
            self.inner.value()
        }

        fn status(&self) -> KvResult<()> {
            self.inner.status()
        }
    }

    struct BatchSink<'a>(&'a mut WriteBatch<crate::kv::mem::MemBatch>);

    impl ColumnSink for BatchSink<'_> {
        fn put(&mut self, column: Column, key: &[u8], value: &[u8]) -> KvResult<()> {
            self.0.put(column, key, value, WriteFlags::NONE)
        }

        fn remove(&mut self, column: Column, key: &[u8]) -> KvResult<()> {
            self.0.delete(column, key, WriteFlags::NONE)
        }
    }

    /// Port of `PreimageStorageKeyTests.StorageScan_WalksOnlyOwnAccountSlots`: two accounts share a mined 4-byte
    /// lead. Both key shapes yield the same slots, but the split shape of `PreimageFlatV1` walks the other account's
    /// slots too, which makes a scan over vanity-prefixed accounts quadratic.
    #[rstest]
    #[case(true, 4)]
    #[case(false, 8)]
    fn a_storage_scan_walks_only_its_account_unless_the_key_is_split(
        #[case] full_address: bool,
        #[case] walked: usize,
    ) {
        const SLOTS: u64 = 4;
        let shape = FlatShape {
            full_address_storage_key: full_address,
            ..shape(true)
        };
        let preimage = |lead: u8| {
            let mut key = [0u8; 32];
            key[4..20].fill(lead);
            key
        };
        let (account_a, account_b) = (preimage(0xAA), preimage(0xBB));

        let kv = MemKv::new();
        let mut batch = kv.write_batch().unwrap();
        for account in [account_a, account_b] {
            for slot in 1..=SLOTS {
                set_storage(
                    &mut BatchSink(&mut batch),
                    &shape,
                    &account,
                    &U256::from_u64(slot).to_be_bytes(),
                    Some(&U256::from_u64(1)),
                )
                .unwrap();
            }
        }
        batch.commit().unwrap();

        let counter = Arc::new(AtomicUsize::new(0));
        let reader = CountingReader {
            inner: kv.reader(false).unwrap(),
            walked: counter.clone(),
        };
        let mut iterator = create_storage_iterator(&reader, &shape, &account_a, &[0; 32], &[0xff; 32]).unwrap();
        let mut slots = Vec::new();
        while iterator.move_next().unwrap() {
            slots.push(U256::from_be_bytes(&iterator.current_key()));
            assert_eq!(iterator.current_value(), [1]);
        }

        assert_eq!(slots, (1..=SLOTS).map(U256::from_u64).collect::<Vec<_>>());
        assert_eq!(counter.load(Ordering::Relaxed), walked);
    }

    fn shape(rlp_wrap_slots: bool) -> FlatShape {
        FlatShape {
            account_column: Column::Account,
            storage_column: Column::Storage,
            rlp_wrap_slots,
            full_address_storage_key: false,
        }
    }

    fn read_stored(stored: &[u8], rlp_wrap_slots: bool) -> Result<Option<U256>, PersistenceError> {
        let kv = MemKv::new();
        let key = crate::codec::keys::storage_key(&[0; 32], &[0; 32], false);
        let mut batch = kv.write_batch().unwrap();
        batch.put(Column::Storage, &key, stored, WriteFlags::NONE).unwrap();
        batch.commit().unwrap();
        try_get_storage(&kv.reader(false).unwrap(), &shape(rlp_wrap_slots), &[0; 32], &[0; 32])
    }

    /// Port of `BaseFlatPersistenceReaderTests`: a raw value over 32 bytes fails loudly instead of corrupting
    /// memory; shorter ones are right-aligned; a wrapped 32-byte value (33 bytes stored) decodes.
    #[rstest]
    #[case(33, false, true)]
    #[case(32, false, false)]
    #[case(16, false, false)]
    #[case(1, false, false)]
    #[case(32, true, false)]
    fn stored_slot_values_are_bounded_and_right_aligned(
        #[case] length: usize,
        #[case] rlp_wrap_slots: bool,
        #[case] throws: bool,
    ) {
        let value: Vec<u8> = (1..=length as u8).collect();
        let stored = if rlp_wrap_slots {
            let mut stored = vec![0x80 + length as u8];
            stored.extend_from_slice(&value);
            stored
        } else {
            value.clone()
        };

        let read = read_stored(&stored, rlp_wrap_slots);
        if throws {
            assert!(
                matches!(read, Err(PersistenceError::InvalidConfiguration(ref message)) if message.contains("33 bytes")),
                "{read:?}"
            );
            return;
        }

        let mut expected = [0u8; 32];
        expected[32 - length..].copy_from_slice(&value);
        assert_eq!(read.unwrap().map(|value| value.to_be_bytes()), Some(expected));
    }

    #[test]
    fn a_value_longer_than_the_read_buffer_fails() {
        assert_eq!(
            read_stored(&[1; 34], false),
            Err(PersistenceError::ValueTooLarge { len: 34, capacity: 33 })
        );
    }
}
