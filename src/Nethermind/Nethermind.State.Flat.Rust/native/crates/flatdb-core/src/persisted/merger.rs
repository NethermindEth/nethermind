//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotMerger.cs`: the N-way merge of persisted
//! snapshots, oldest first, into one table.
//!
//! The merge walks the sources in ascending key order and keeps the newest source's value of each key; node references
//! are copied as they are, so the merged table points into the sources' blob files, and the ref-id records merge into
//! their union. In a per-address group the self-destruct record sorts before the slots, so it sets the truncation
//! barrier (the newest source that destructed the account) before the slots stream past it: a slot whose newest
//! source predates the barrier is dropped, without a bloom key, as C# drops it. The find-min is O(N) per step, as C#.

use super::io::{ByteReader, TableSink};
use super::keys::{
    self, ACCOUNT_COLUMN, MAX_KEY_LENGTH, METADATA_COLUMN, REF_ID_COLUMN, SELF_DESTRUCT_DESTRUCTED_MARKER,
    SELF_DESTRUCT_SUB, SLOT_SUB, STORAGE_COLUMN,
};
use super::reader::read_value;
use super::sorted::enumerator::SortedTableEnumerator;
use super::sorted::table_builder::SortedTableBuilder;
use super::sorted::table_reader;
use super::{TierError, TierResult, bloom_keys};
use crate::bloom::BloomFilter;

/// `NWayMergeSnapshots`: merges the tables of `sources`, oldest first, into `sink`, adding the merged keys to `bloom`.
pub fn merge(sources: &[ByteReader<'_>], sink: &mut impl TableSink, bloom: &mut BloomFilter) -> TierResult<()> {
    if sources.is_empty() {
        return Err(TierError::InvalidArgument(
            "A merge needs at least one source.".to_owned(),
        ));
    }
    let mut table = SortedTableBuilder::new(sink);
    merge_entries(sources, &mut table, bloom)?;
    merge_metadata(sources, &mut table)?;
    table.build()
}

/// `MergeEntries`: every record below the metadata column, newest source first.
fn merge_entries<S: TableSink>(
    sources: &[ByteReader<'_>],
    table: &mut SortedTableBuilder<'_, S>,
    bloom: &mut BloomFilter,
) -> TierResult<()> {
    let mut cursors = sources.iter().map(SortedTableEnumerator::new).collect::<Vec<_>>();
    let mut has_more = Vec::with_capacity(sources.len());
    for (cursor, reader) in cursors.iter_mut().zip(sources) {
        has_more.push(keys::move_next_checked(cursor, reader)?);
    }

    // The current per-address group: its address and bloom key, and its self-destruct barrier (-1 for none).
    let mut current_address: Option<[u8; 20]> = None;
    let mut address_key = 0u64;
    let mut barrier = -1isize;
    let mut matching = Vec::with_capacity(sources.len());
    let mut key_buffer = [0u8; MAX_KEY_LENGTH];

    loop {
        let mut minimum: Option<usize> = None;
        for index in 0..sources.len() {
            if has_more[index]
                && minimum.is_none_or(|minimum| cursors[index].current_key() < cursors[minimum].current_key())
            {
                minimum = Some(index);
            }
        }
        let Some(minimum) = minimum else {
            break;
        };

        let key_length = cursors[minimum].current_key().len();
        key_buffer[..key_length].copy_from_slice(cursors[minimum].current_key());
        let key = &key_buffer[..key_length];
        // Metadata sorts last and is merged by merge_metadata.
        if key[0] == METADATA_COLUMN {
            break;
        }

        let is_per_address = key[0] == ACCOUNT_COLUMN;
        if is_per_address && current_address.is_none_or(|address| address != keys::per_address_address(key)) {
            let address: [u8; 20] = keys::per_address_address(key).try_into().expect("20 bytes");
            address_key = bloom_keys::address_key(&address);
            bloom.add(address_key);
            current_address = Some(address);
            barrier = -1;
        }

        matching.clear();
        matching.extend((0..sources.len()).filter(|&index| has_more[index] && cursors[index].current_key() == key));
        let newest = *matching.last().expect("the minimum matches");

        if is_per_address && keys::per_address_sub_column(key) == SELF_DESTRUCT_SUB {
            barrier = self_destruct_barrier(sources, &cursors, &matching);
            let marker = if barrier >= 0 {
                SELF_DESTRUCT_DESTRUCTED_MARKER
            } else {
                keys::SELF_DESTRUCT_NEW_MARKER
            };
            table.add(key, &[marker])?;
        } else if is_per_address && keys::per_address_sub_column(key) == SLOT_SUB {
            if barrier < 0 || newest as isize >= barrier {
                table.add(key, newest_value(sources, &cursors, newest)?)?;
                bloom.add(bloom_keys::slot_key(address_key, keys::slot_key_bytes(key)));
            }
        } else {
            table.add(key, newest_value(sources, &cursors, newest)?)?;
            add_bloom_for_key(bloom, key);
        }

        for &index in &matching {
            has_more[index] = keys::move_next_checked(&mut cursors[index], &sources[index])?;
        }
    }
    Ok(())
}

fn newest_value<'a>(
    sources: &[ByteReader<'a>],
    cursors: &[SortedTableEnumerator],
    newest: usize,
) -> TierResult<&'a [u8]> {
    let (offset, length) = cursors[newest].current_value();
    read_value(&sources[newest], offset, length)
}

/// `ComputeSelfDestructBarrier`: the newest matching source whose self-destruct record says destructed, -1 for none.
/// An unreadable flag is skipped rather than read as "destructed".
fn self_destruct_barrier(sources: &[ByteReader<'_>], cursors: &[SortedTableEnumerator], matching: &[usize]) -> isize {
    let mut barrier = -1;
    for &index in matching {
        if sources[index].read_u8(cursors[index].current_value().0) == Some(SELF_DESTRUCT_DESTRUCTED_MARKER) {
            barrier = index as isize;
        }
    }
    barrier
}

/// `AddBloomForKey`: trie-node keys; ref-ids are not bloom-gated and the address key is added once per group.
fn add_bloom_for_key(bloom: &mut BloomFilter, key: &[u8]) {
    match key[0] {
        REF_ID_COLUMN | ACCOUNT_COLUMN => {}
        STORAGE_COLUMN => bloom.add(bloom_keys::storage_node_key_of_table_key(key)),
        _ => bloom.add(bloom_keys::state_path_key_of_encoded(keys::state_path_bytes(key))),
    }
}

/// `MergeMetadata`: `from_*` of the oldest source, the `noderefs` marker, `to_*` of the newest. The sources'
/// `blob_range` is not carried over: a merged table reaches the blob files through its ref-id records.
fn merge_metadata<S: TableSink>(sources: &[ByteReader<'_>], table: &mut SortedTableBuilder<'_, S>) -> TierResult<()> {
    let (oldest, newest) = (&sources[0], &sources[sources.len() - 1]);
    add_metadata_field(table, oldest, keys::METADATA_FROM_BLOCK)?;
    add_metadata_field(table, oldest, keys::METADATA_FROM_HASH)?;
    table.add(
        keys::metadata_key(keys::METADATA_NODE_REFS).as_slice(),
        &[keys::METADATA_NODE_REFS_PRESENT_MARKER],
    )?;
    add_metadata_field(table, newest, keys::METADATA_TO_BLOCK)?;
    add_metadata_field(table, newest, keys::METADATA_TO_HASH)
}

fn add_metadata_field<S: TableSink>(
    table: &mut SortedTableBuilder<'_, S>,
    source: &ByteReader<'_>,
    name: &[u8; keys::METADATA_NAME_LENGTH],
) -> TierResult<()> {
    let key = keys::metadata_key(name);
    match table_reader::try_seek(source, key.as_slice())? {
        Some((offset, length)) => table.add(key.as_slice(), read_value(source, offset, length)?),
        None => Ok(()),
    }
}
