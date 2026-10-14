//! Persisted-tier test support: snapshots from the C# record stream, and persisted snapshots held in memory (a table
//! buffer, a bloom, and blob files shared by the tier).

use std::collections::BTreeMap;
use std::ops::{Deref, DerefMut};
use std::sync::{Arc, RwLock};

use flatdb_core::bloom::BloomFilter;
use flatdb_core::persisted::builder::{self, BuildOutput};
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::merger;
use flatdb_core::persisted::reader::{BlobSource, TableView};
use flatdb_core::persisted::stack::PersistedLayer;
use flatdb_core::persisted::storage::blob_writer::BlobWriter;
use flatdb_core::persisted::{TierError, TierResult};
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};

/// The blob files of a tier, by id, each from offset 0.
pub type Blobs = Arc<RwLock<MemBlobs>>;

/// Blob files held in memory, by id, each from offset 0.
#[derive(Default)]
pub struct MemBlobs(pub BTreeMap<u16, Vec<u8>>);

impl Deref for MemBlobs {
    type Target = BTreeMap<u16, Vec<u8>>;

    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl DerefMut for MemBlobs {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}

impl BlobSource for MemBlobs {
    fn random_read(&self, blob_arena_id: u16, offset: i32, buffer: &mut [u8]) -> TierResult<usize> {
        let file = self
            .get(&blob_arena_id)
            .ok_or_else(|| TierError::InvalidOperation(format!("no blob file {blob_arena_id}")))?;
        let start = usize::try_from(offset).unwrap_or(usize::MAX).min(file.len());
        let read = buffer.len().min(file.len() - start);
        buffer[..read].copy_from_slice(&file[start..start + read]);
        Ok(read)
    }
}

/// A snapshot from the record stream of `flatdb-ffi/src/snapshot_stream.rs`, which the C# golden generator writes.
pub fn snapshot_from_records(from: StateId, to: StateId, records: &[u8]) -> Snapshot {
    let mut reader = FieldReader {
        bytes: records,
        position: 0,
    };
    let mut builder = SnapshotContentBuilder::new();
    while reader.position < records.len() {
        match reader.u8() {
            0 => {
                let address = reader.address();
                builder.set_account(address, Some(reader.account()));
            }
            1 => builder.set_account(reader.address(), None),
            2 => {
                let (address, slot, value) = (reader.address(), reader.u256(), reader.u256());
                builder.set_storage(address, slot, Some(value));
            }
            3 => {
                let (address, slot) = (reader.address(), reader.u256());
                builder.set_storage(address, slot, None);
            }
            4 => {
                let address = reader.address();
                builder.set_self_destruct(address, reader.u8() != 0);
            }
            5 => {
                let (path, hash) = (reader.path(), reader.hash());
                builder.set_state_node(path, hash, reader.bytes());
            }
            6 => {
                let (address_hash, path, hash) = (reader.hash(), reader.path(), reader.hash());
                builder.set_storage_node(address_hash, path, hash, reader.bytes());
            }
            tag => panic!("unknown record tag {tag}"),
        }
    }
    Snapshot::new(from, to, builder.build())
}

struct FieldReader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> FieldReader<'a> {
    fn take(&mut self, count: usize) -> &'a [u8] {
        let taken = &self.bytes[self.position..self.position + count];
        self.position += count;
        taken
    }

    fn u8(&mut self) -> u8 {
        self.take(1)[0]
    }

    fn u64(&mut self) -> u64 {
        u64::from_le_bytes(self.take(8).try_into().unwrap())
    }

    fn address(&mut self) -> Address {
        self.take(20).try_into().unwrap()
    }

    fn hash(&mut self) -> Hash256 {
        self.take(32).try_into().unwrap()
    }

    fn u256(&mut self) -> U256 {
        U256([self.u64(), self.u64(), self.u64(), self.u64()])
    }

    fn account(&mut self) -> Account {
        Account::with_hashes(self.u64(), self.u256(), self.hash(), self.hash())
    }

    fn path(&mut self) -> TreePath {
        let path = self.hash();
        TreePath::new(path, self.u8())
    }

    fn bytes(&mut self) -> &'a [u8] {
        let length = u32::from_le_bytes(self.take(4).try_into().unwrap()) as usize;
        self.take(length)
    }
}

/// A persisted snapshot in memory: its table bytes, its bloom (an `Arc`, shared as C# shares a
/// `RefCountedBloomFilter`), and the tier's blob files.
pub struct MemSnapshot {
    pub from: StateId,
    pub to: StateId,
    pub tier: SnapshotTier,
    pub table: Vec<u8>,
    pub bloom: Arc<BloomFilter>,
    pub blobs: Blobs,
}

impl MemSnapshot {
    /// A twin over the same table with another bloom, as `ShareBloomAt` registers.
    pub fn with_bloom(&self, bloom: Arc<BloomFilter>) -> Self {
        Self {
            from: self.from,
            to: self.to,
            tier: self.tier,
            table: self.table.clone(),
            bloom,
            blobs: self.blobs.clone(),
        }
    }

    fn view<T>(&self, read: impl FnOnce(&TableView<'_, '_>) -> TierResult<T>) -> TierResult<T> {
        let blobs = self.blobs.read().unwrap();
        let reader = ByteReader::new(&self.table);
        read(&TableView::new(&reader, &*blobs))
    }
}

impl PersistedLayer for MemSnapshot {
    fn bloom(&self) -> &BloomFilter {
        &self.bloom
    }

    fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>> {
        self.view(|view| view.try_get_account(address))
    }

    fn try_get_self_destruct_flag(&self, address: &Address) -> TierResult<Option<bool>> {
        self.view(|view| view.try_get_self_destruct_flag(address))
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> TierResult<Option<Option<U256>>> {
        self.view(|view| view.try_get_slot(address, slot))
    }

    fn try_load_state_node_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.view(|view| view.try_load_state_node_rlp(path))
    }

    fn try_load_storage_node_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.view(|view| view.try_load_storage_node_rlp(address_hash, path))
    }
}

/// A base snapshot built from `snapshot`, its RLP appended to blob file `blob_id`, with the 1-line bloom C# converts
/// with (or the always-true one when `bits_per_key` is 0).
pub fn convert(snapshot: &Snapshot, blobs: &Blobs, blob_id: u16, bits_per_key: f64) -> (MemSnapshot, BuildOutput) {
    let mut bloom = if bits_per_key > 0.0 {
        BloomFilter::new(1, bits_per_key).unwrap()
    } else {
        BloomFilter::always_true()
    };
    let start = blobs.read().unwrap().get(&blob_id).map_or(0, Vec::len) as i64;
    let mut blob_writer = BlobWriter::new(Vec::new(), blob_id, start);
    let mut table = Vec::new();
    let output = builder::build(snapshot, &mut table, &mut blob_writer, &mut bloom).unwrap();
    let appended = blob_writer.finish().unwrap();
    blobs
        .write()
        .unwrap()
        .entry(blob_id)
        .or_default()
        .extend_from_slice(&appended);
    let persisted = MemSnapshot {
        from: snapshot.from(),
        to: snapshot.to(),
        tier: SnapshotTier::PersistedBase,
        table,
        bloom: Arc::new(bloom),
        blobs: blobs.clone(),
    };
    (persisted, output)
}

/// The merge of `sources`, oldest first, into a snapshot of `tier` with `bloom`.
pub fn merge(sources: &[&MemSnapshot], tier: SnapshotTier, mut bloom: BloomFilter) -> MemSnapshot {
    let readers = sources
        .iter()
        .map(|source| ByteReader::new(&source.table))
        .collect::<Vec<_>>();
    let mut table = Vec::new();
    merger::merge(&readers, &mut table, &mut bloom).unwrap();
    MemSnapshot {
        from: sources[0].from,
        to: sources[sources.len() - 1].to,
        tier,
        table,
        bloom: Arc::new(bloom),
        blobs: sources[0].blobs.clone(),
    }
}

pub fn new_blobs() -> Blobs {
    Arc::new(RwLock::new(MemBlobs::default()))
}
