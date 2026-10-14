//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshot.cs`: one persisted snapshot, its table in an
//! arena reservation and its trie-node RLP in the blob files its ref-id records name.
//!
//! C# refcounts the snapshot; here its leases are the clones of an `Arc`, and `CleanUp` is `Drop`, in the C# order: the
//! blob leases first (a file left with only the manager's lease is reset to empty for reuse), then the reservation,
//! then the bloom. A twin, the same table under another bloom (`SnapshotRepository::share_bloom_across_range`), leases
//! the reservation and the blob files on its own.

use std::io;
use std::sync::Arc;

use super::bloom_keys::PersistedBloom;
use super::codec::{self, BlobRange, SnapshotLocation};
use super::reader::{self, BlobSource, TableView};
use super::stack::PersistedLayer;
use super::storage::arena_reservation::ArenaReservation;
use super::storage::blob_file::BlobFile;
use super::storage::blob_manager::BlobManager;
use super::storage::whole_read_session::WholeReadSession;
use super::{TierError, TierResult};
use crate::bloom::BloomFilter;
use crate::snapshot::SnapshotTier;
use crate::stats::{TierGaugeRow, TierStats};
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

/// What a persisted snapshot is beside its bytes: its range, its tier, and whether its content may clear an account's
/// storage, `true` when unknown, as for a snapshot loaded from disk.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SnapshotHeader {
    pub from: StateId,
    pub to: StateId,
    pub tier: SnapshotTier,
    pub may_have_self_destructs: bool,
}

pub struct PersistedSnapshot {
    header: SnapshotHeader,
    blob_range: BlobRange,
    /// Leases of the blob files the ref-id records name, by ascending id; taken out by `Drop`.
    blob_files: Vec<Arc<BlobFile>>,
    reservation: Arc<ArenaReservation>,
    bloom: Arc<PersistedBloom>,
    blobs: Arc<BlobManager>,
    stats: Arc<TierStats>,
}

impl PersistedSnapshot {
    /// The snapshot whose table `reservation` holds: leases every blob file its ref-id records name, failing, with
    /// nothing leased, when one is not registered.
    pub fn open(
        header: SnapshotHeader,
        reservation: Arc<ArenaReservation>,
        bloom: Arc<PersistedBloom>,
        blobs: Arc<BlobManager>,
        stats: Arc<TierStats>,
    ) -> TierResult<Self> {
        let table = reservation.reader();
        let blob_range = reader::read_blob_range(&table)?;
        let blob_files = reader::read_ref_ids(&table)?
            .into_iter()
            .map(|id| {
                blobs.try_lease_file(id).ok_or_else(|| {
                    TierError::InvalidOperation(format!("Blob arena {id} not registered with the blob manager"))
                })
            })
            .collect::<TierResult<Vec<_>>>()?;

        stats.gauges.add(
            header.tier,
            codec::depth(&header.from, &header.to),
            TierGaugeRow {
                active: 1,
                ..TierGaugeRow::default()
            },
        );
        Ok(Self {
            header,
            blob_range,
            blob_files,
            reservation,
            bloom,
            blobs,
            stats,
        })
    }

    /// A twin of this snapshot reading through `bloom`, as `ShareBloomAt` builds.
    pub fn twin(&self, bloom: Arc<PersistedBloom>) -> TierResult<Self> {
        Self::open(
            self.header,
            self.reservation.clone(),
            bloom,
            self.blobs.clone(),
            self.stats.clone(),
        )
    }

    pub fn header(&self) -> SnapshotHeader {
        self.header
    }

    pub fn from(&self) -> StateId {
        self.header.from
    }

    pub fn to(&self) -> StateId {
        self.header.to
    }

    pub fn tier(&self) -> SnapshotTier {
        self.header.tier
    }

    /// `Size`: the table's bytes.
    pub fn size(&self) -> i64 {
        self.reservation.size()
    }

    pub fn location(&self) -> SnapshotLocation {
        self.reservation.location()
    }

    /// `BloomRef`: the bloom, which twins share by its `Arc`.
    pub fn persisted_bloom(&self) -> &Arc<PersistedBloom> {
        &self.bloom
    }

    /// `BlobRange`: the run of trie-node RLP a base wrote, [`BlobRange::NONE`] for a compacted snapshot.
    pub fn blob_range(&self) -> BlobRange {
        self.blob_range
    }

    pub fn reservation(&self) -> &Arc<ArenaReservation> {
        &self.reservation
    }

    /// `BeginWholeReadSession`: a separate mapping of the table for one sequential scan.
    pub fn begin_whole_read_session(&self) -> io::Result<WholeReadSession> {
        self.reservation.begin_whole_read_session()
    }

    /// `AdviseWillNeedBlobRange`: read-ahead of a base's RLP run before it is scanned.
    pub fn advise_will_need_blob_range(&self) {
        if let Some(file) = self.blob_range_file() {
            file.fadvise_will_need(self.blob_range.offset, self.blob_range.length);
        }
    }

    /// `AdviseDontNeedBlobRange`: drops a base's RLP run from the OS file cache.
    pub fn advise_dont_need_blob_range(&self) {
        if let Some(file) = self.blob_range_file() {
            file.fadvise_dont_need(self.blob_range.offset, self.blob_range.length);
        }
    }

    fn blob_range_file(&self) -> Option<&Arc<BlobFile>> {
        if self.blob_range.is_empty() {
            return None;
        }
        self.leased_file(self.blob_range.blob_arena_id)
    }

    /// `Demote`: drops the table's pages from the mapping, the OS file cache and the page tracker; it stays readable.
    pub fn demote(&self) {
        self.reservation.advise_and_fadvise_dont_need();
    }

    /// `PersistOnShutdown`: keeps the table and every blob file it names on disk for the next session.
    pub fn persist_on_shutdown(&self) {
        self.reservation.persist_on_shutdown();
        for file in &self.blob_files {
            file.persist_on_shutdown();
        }
    }

    fn leased_file(&self, id: u16) -> Option<&Arc<BlobFile>> {
        self.blob_files
            .binary_search_by_key(&id, |file| file.id())
            .ok()
            .map(|index| &self.blob_files[index])
    }

    fn read<T>(&self, read: impl FnOnce(&TableView<'_, '_>) -> TierResult<T>) -> TierResult<T> {
        let table = self.reservation.reader();
        read(&TableView::new(&table, self))
    }
}

impl BlobSource for PersistedSnapshot {
    /// The snapshot's own lease of a file it names; the manager's for any other id, as C# resolves every id there.
    fn random_read(&self, blob_arena_id: u16, offset: i32, buffer: &mut [u8]) -> TierResult<usize> {
        match self.leased_file(blob_arena_id) {
            Some(file) => Ok(file.random_read(offset as u64, buffer)?),
            None => self.blobs.random_read(blob_arena_id, offset, buffer),
        }
    }
}

impl PersistedLayer for PersistedSnapshot {
    fn bloom(&self) -> &BloomFilter {
        self.bloom.filter()
    }

    fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>> {
        self.read(|view| view.try_get_account(address))
    }

    fn try_get_self_destruct_flag(&self, address: &Address) -> TierResult<Option<bool>> {
        self.read(|view| view.try_get_self_destruct_flag(address))
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> TierResult<Option<Option<U256>>> {
        self.read(|view| view.try_get_slot(address, slot))
    }

    fn try_load_state_node_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.read(|view| view.try_load_state_node_rlp(path))
    }

    fn try_load_storage_node_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.read(|view| view.try_load_storage_node_rlp(address_hash, path))
    }
}

impl Drop for PersistedSnapshot {
    fn drop(&mut self) {
        for file in std::mem::take(&mut self.blob_files) {
            self.blobs.release_lease(file);
        }
        self.stats.gauges.add(
            self.header.tier,
            codec::depth(&self.header.from, &self.header.to),
            TierGaugeRow {
                active: -1,
                ..TierGaugeRow::default()
            },
        );
    }
}
