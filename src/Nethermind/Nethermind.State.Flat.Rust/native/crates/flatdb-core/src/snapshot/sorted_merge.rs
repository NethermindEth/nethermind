//! Port of `Nethermind.State.Flat/Collections/SortedMergeDictionary.cs`: a build-once map whose entries are sorted by
//! key, with a hash index over them, so lookups are O(1) and iteration is in key order.
//!
//! The C# class pools its arrays and salts its buckets to reuse them across builds; here a map is built once and
//! dropped, so only its semantics are kept: [`SortedMergeMap::from_unsorted`] sorts by [`SnapshotKey::compare`],
//! lookups match by key equality, and [`SortedMergeMap::merge`] k-way merges sorted runs where, among the entries of
//! equal keys the keep predicate accepts, the one of the highest-index source wins.

use std::cmp::Ordering;
use std::collections::BinaryHeap;
use std::hash::BuildHasher;

use hashbrown::{DefaultHashBuilder, HashTable};

use super::keys::SnapshotKey;

pub struct SortedMergeMap<K, V> {
    entries: Vec<(K, V)>,
    index: HashTable<u32>,
    hasher: DefaultHashBuilder,
}

impl<K: SnapshotKey, V> Default for SortedMergeMap<K, V> {
    fn default() -> Self {
        Self::from_sorted(Vec::new())
    }
}

impl<K: SnapshotKey, V> SortedMergeMap<K, V> {
    /// Port of `BuildFromUnsorted`: the entries sorted by key. The keys must be distinct, as a dictionary's are.
    pub fn from_unsorted(entries: impl IntoIterator<Item = (K, V)>) -> Self {
        Self::from_sorted(sort_run(entries))
    }

    /// Port of `BuildFromMerge`: merges runs sorted by key with distinct keys, in ascending priority.
    ///
    /// For every key, the entry of the highest-index source that `keep(source, key)` accepts is kept, as
    /// `map(source, value)`; a key no source keeps is omitted.
    pub fn merge(
        sources: &[&[(K, V)]],
        mut keep: impl FnMut(usize, &K) -> bool,
        mut map: impl FnMut(usize, &V) -> V,
    ) -> Self {
        let total = sources.iter().map(|source| source.len()).sum();
        let mut entries = Vec::with_capacity(total);
        let mut heads: BinaryHeap<Head<'_, K>> = sources
            .iter()
            .enumerate()
            .filter_map(|(source, run)| {
                run.first().map(|(key, _)| Head {
                    key,
                    source,
                    position: 0,
                })
            })
            .collect();

        while let Some(first) = heads.pop() {
            let key = first.key;
            let mut chosen: Option<(usize, usize)> = None;
            let mut next = Some(first);
            // Equal keys pop in ascending source order, so the last kept entry is the highest-priority one.
            while let Some(head) = next {
                if keep(head.source, head.key) {
                    chosen = Some((head.source, head.position));
                }
                if let Some((next_key, _)) = sources[head.source].get(head.position + 1) {
                    heads.push(Head {
                        key: next_key,
                        source: head.source,
                        position: head.position + 1,
                    });
                }
                next = if heads
                    .peek()
                    .is_some_and(|candidate| candidate.key.compare(key) == Ordering::Equal)
                {
                    heads.pop()
                } else {
                    None
                };
            }

            if let Some((source, position)) = chosen {
                let (chosen_key, value) = &sources[source][position];
                entries.push((*chosen_key, map(source, value)));
            }
        }

        Self::from_sorted(entries)
    }

    fn from_sorted(entries: Vec<(K, V)>) -> Self {
        let hasher = DefaultHashBuilder::default();
        let mut index = HashTable::with_capacity(entries.len());
        let hash_of = |position: &u32| hasher.hash_one(entries[*position as usize].0);
        for (position, (key, _)) in entries.iter().enumerate() {
            let position = u32::try_from(position).expect("a sorted map holds at most u32::MAX entries");
            index.insert_unique(hasher.hash_one(key), position, hash_of);
        }

        Self { entries, index, hasher }
    }

    pub fn get(&self, key: &K) -> Option<&V> {
        let entries = &self.entries;
        self.index
            .find(self.hasher.hash_one(key), |position| {
                entries[*position as usize].0 == *key
            })
            .map(|position| &entries[*position as usize].1)
    }

    pub fn contains_key(&self, key: &K) -> bool {
        self.get(key).is_some()
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// The entries in key order.
    pub fn entries(&self) -> &[(K, V)] {
        &self.entries
    }

    pub fn iter(&self) -> impl ExactSizeIterator<Item = (&K, &V)> {
        self.entries.iter().map(|(key, value)| (key, value))
    }
}

/// Port of `BuildRunFromUnsorted`: the entries as a run sorted by key, for [`SortedMergeMap::merge`].
pub fn sort_run<K: SnapshotKey, V>(entries: impl IntoIterator<Item = (K, V)>) -> Vec<(K, V)> {
    let mut run: Vec<(K, V)> = entries.into_iter().collect();
    run.sort_unstable_by(|left, right| left.0.compare(&right.0));
    run
}

/// The head of a source in the merge; the heap pops the smallest key, ties by the lowest source.
struct Head<'a, K> {
    key: &'a K,
    source: usize,
    position: usize,
}

impl<K: SnapshotKey> Ord for Head<'_, K> {
    fn cmp(&self, other: &Self) -> Ordering {
        other.key.compare(self.key).then_with(|| other.source.cmp(&self.source))
    }
}

impl<K: SnapshotKey> PartialOrd for Head<'_, K> {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl<K: SnapshotKey> PartialEq for Head<'_, K> {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl<K: SnapshotKey> Eq for Head<'_, K> {}
