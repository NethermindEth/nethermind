use super::{KvIterator, KvResult};

/// An iterator over entries collected when it was opened, for backends without native iterators.
pub struct CollectedIter<V> {
    entries: Vec<(Box<[u8]>, V)>,
    position: Option<usize>,
}

impl<V: AsRef<[u8]> + Send> CollectedIter<V> {
    /// Iterates `entries`, which must be sorted by key.
    pub fn new(entries: Vec<(Box<[u8]>, V)>) -> Self {
        Self {
            entries,
            position: None,
        }
    }

    fn entry(&self) -> &(Box<[u8]>, V) {
        &self.entries[self.position.expect("the iterator is not positioned on an entry")]
    }
}

impl<V: AsRef<[u8]> + Send> KvIterator for CollectedIter<V> {
    fn seek(&mut self, key: &[u8]) {
        let index = self.entries.partition_point(|(entry, _)| &**entry < key);
        self.position = (index < self.entries.len()).then_some(index);
    }

    fn seek_for_prev(&mut self, key: &[u8]) {
        let index = self.entries.partition_point(|(entry, _)| &**entry <= key);
        self.position = index.checked_sub(1);
    }

    fn seek_to_first(&mut self) {
        self.position = (!self.entries.is_empty()).then_some(0);
    }

    fn seek_to_last(&mut self) {
        self.position = self.entries.len().checked_sub(1);
    }

    fn valid(&self) -> bool {
        self.position.is_some()
    }

    fn next(&mut self) {
        let next = self.position.expect("the iterator is not positioned on an entry") + 1;
        self.position = (next < self.entries.len()).then_some(next);
    }

    fn prev(&mut self) {
        self.position = self
            .position
            .expect("the iterator is not positioned on an entry")
            .checked_sub(1);
    }

    fn key(&self) -> &[u8] {
        &self.entry().0
    }

    fn value(&self) -> &[u8] {
        self.entry().1.as_ref()
    }

    fn status(&self) -> KvResult<()> {
        Ok(())
    }
}
