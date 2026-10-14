//! Port of the formats of `Nethermind.State.Flat/PersistedSnapshots/Storage/`: `BlobRange.cs`, `SnapshotLocation.cs`,
//! `CatalogEntry.cs` and the rows of `SnapshotCatalog.cs`.

use super::{TierError, TierResult};
use crate::snapshot::SnapshotTier;
use crate::types::StateId;

/// The contiguous run of trie-node RLP a base snapshot wrote into one blob file. Compacted snapshots reference blob
/// files through their ref-id records and carry [`BlobRange::NONE`].
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BlobRange {
    pub blob_arena_id: u16,
    pub offset: i64,
    pub length: i64,
}

impl BlobRange {
    pub const NONE: Self = Self {
        blob_arena_id: 0,
        offset: 0,
        length: 0,
    };

    /// `SerializedSize`: id, offset and length, little-endian.
    pub const SERIALIZED_SIZE: usize = 18;

    pub fn is_empty(&self) -> bool {
        self.length == 0
    }

    pub fn to_bytes(self) -> [u8; Self::SERIALIZED_SIZE] {
        let mut bytes = [0u8; Self::SERIALIZED_SIZE];
        bytes[..2].copy_from_slice(&self.blob_arena_id.to_le_bytes());
        bytes[2..10].copy_from_slice(&self.offset.to_le_bytes());
        bytes[10..].copy_from_slice(&self.length.to_le_bytes());
        bytes
    }

    /// The range of a `blob_range` value, [`BlobRange::NONE`] for a value of another length, as `ReadBlobRange`.
    pub fn read(bytes: &[u8]) -> Self {
        if bytes.len() != Self::SERIALIZED_SIZE {
            return Self::NONE;
        }
        Self {
            blob_arena_id: u16::from_le_bytes([bytes[0], bytes[1]]),
            offset: i64::from_le_bytes(bytes[2..10].try_into().expect("8 bytes")),
            length: i64::from_le_bytes(bytes[10..].try_into().expect("8 bytes")),
        }
    }
}

/// Where a snapshot's table lies: the arena file and the reservation in it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SnapshotLocation {
    pub arena_id: i32,
    pub offset: i64,
    pub size: i64,
}

/// A catalog row: a snapshot's range, its table's location and its tier.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CatalogEntry {
    pub from: StateId,
    pub to: StateId,
    pub location: SnapshotLocation,
    pub tier: SnapshotTier,
}

/// `KeySize`: `To.BlockNumber` (i64 BE), `To.StateRoot`, `depth` (i64 BE).
pub const CATALOG_KEY_SIZE: usize = 48;

/// `EntrySize`: the packed little-endian `EntryBytes`.
pub const CATALOG_VALUE_SIZE: usize = 101;

/// `To − From` as C# computes it: in `ulong`, wrapping, so a base from PreGenesis spans 1.
pub fn depth(from: &StateId, to: &StateId) -> i64 {
    to.block_number.wrapping_sub(from.block_number) as i64
}

/// `WriteKey`.
pub fn catalog_key(to: &StateId, depth: i64) -> [u8; CATALOG_KEY_SIZE] {
    let mut key = [0u8; CATALOG_KEY_SIZE];
    key[..8].copy_from_slice(&to.block_number.to_be_bytes());
    key[8..40].copy_from_slice(&to.state_root);
    key[40..].copy_from_slice(&depth.to_be_bytes());
    key
}

/// The row `SnapshotCatalog.Add` writes. Block numbers keep their bits, so PreGenesis is −1.
pub fn encode_row(entry: &CatalogEntry) -> ([u8; CATALOG_KEY_SIZE], [u8; CATALOG_VALUE_SIZE]) {
    let mut value = [0u8; CATALOG_VALUE_SIZE];
    value[..8].copy_from_slice(&entry.from.block_number.to_le_bytes());
    value[8..40].copy_from_slice(&entry.from.state_root);
    value[40..48].copy_from_slice(&entry.to.block_number.to_le_bytes());
    value[48..80].copy_from_slice(&entry.to.state_root);
    value[80..84].copy_from_slice(&entry.location.arena_id.to_le_bytes());
    value[84..92].copy_from_slice(&entry.location.offset.to_le_bytes());
    value[92..100].copy_from_slice(&entry.location.size.to_le_bytes());
    value[100] = tier_byte(entry.tier);
    (catalog_key(&entry.to, depth(&entry.from, &entry.to)), value)
}

/// `ReadEntry`: a tier below the persisted ones is the C# load error; one above them, which C# lets through to fail
/// when the snapshot is labelled, fails here.
pub fn decode_value(value: &[u8; CATALOG_VALUE_SIZE]) -> TierResult<CatalogEntry> {
    let u64_at = |at: usize| u64::from_le_bytes(value[at..at + 8].try_into().expect("8 bytes"));
    let i64_at = |at: usize| i64::from_le_bytes(value[at..at + 8].try_into().expect("8 bytes"));
    let hash_at = |at: usize| value[at..at + 32].try_into().expect("32 bytes");
    Ok(CatalogEntry {
        from: StateId::new(u64_at(0), hash_at(8)),
        to: StateId::new(u64_at(40), hash_at(48)),
        location: SnapshotLocation {
            arena_id: i32::from_le_bytes(value[80..84].try_into().expect("4 bytes")),
            offset: i64_at(84),
            size: i64_at(92),
        },
        tier: tier_from_byte(value[100])?,
    })
}

/// The C# rows `IDb.GetAll` returned, packed as `[u32 LE key length][key][u32 LE value length][value]…`, as
/// `SnapshotCatalog.Load` reads them: rows of another key or value length are skipped.
pub fn parse_rows(packed: &[u8]) -> TierResult<Vec<CatalogEntry>> {
    let mut entries = Vec::new();
    let mut position = 0;
    while position < packed.len() {
        let key = take_field(packed, &mut position)?;
        let value = take_field(packed, &mut position)?;
        if key.len() != CATALOG_KEY_SIZE {
            continue;
        }
        if let Ok(value) = <&[u8; CATALOG_VALUE_SIZE]>::try_from(value) {
            entries.push(decode_value(value)?);
        }
    }
    Ok(entries)
}

fn take_field<'a>(packed: &'a [u8], position: &mut usize) -> TierResult<&'a [u8]> {
    let truncated = || TierError::InvalidArgument("The packed catalog rows are truncated.".to_owned());
    let length_bytes = packed.get(*position..*position + 4).ok_or_else(truncated)?;
    let length = u32::from_le_bytes(length_bytes.try_into().expect("4 bytes")) as usize;
    let start = *position + 4;
    let field = start
        .checked_add(length)
        .and_then(|end| packed.get(start..end))
        .ok_or_else(truncated)?;
    *position = start + length;
    Ok(field)
}

/// The on-disk byte of a persisted tier, its `SnapshotTier` value.
///
/// # Panics
/// On an in-memory tier, which is never stored.
pub fn tier_byte(tier: SnapshotTier) -> u8 {
    match tier {
        SnapshotTier::PersistedBase => 2,
        SnapshotTier::PersistedSmallCompacted => 3,
        SnapshotTier::PersistedCompactSized => 4,
        SnapshotTier::PersistedLargeCompacted => 5,
        SnapshotTier::InMemoryBase | SnapshotTier::InMemoryCompacted => {
            panic!("in-memory tier {tier:?} has no catalog byte")
        }
    }
}

pub fn tier_from_byte(byte: u8) -> TierResult<SnapshotTier> {
    match byte {
        2 => Ok(SnapshotTier::PersistedBase),
        3 => Ok(SnapshotTier::PersistedSmallCompacted),
        4 => Ok(SnapshotTier::PersistedCompactSized),
        5 => Ok(SnapshotTier::PersistedLargeCompacted),
        0 | 1 => Err(TierError::InvalidOperation(format!(
            "Persisted snapshot catalog entry has non-persisted tier byte {byte} (only Persisted* tiers are ever stored). \
             The persistedSnapshot/ directory has an incompatible or corrupted layout — wipe and resync."
        ))),
        _ => Err(TierError::InvalidArgument(format!(
            "Not a persisted tier. (Parameter 'tier')\nActual value was {byte}."
        ))),
    }
}

#[cfg(test)]
mod tests {
    use super::{BlobRange, CatalogEntry, SnapshotLocation, decode_value, encode_row, parse_rows, tier_from_byte};
    use crate::persisted::TierError;
    use crate::snapshot::SnapshotTier;
    use crate::types::StateId;

    fn pack(rows: &[(&[u8], &[u8])]) -> Vec<u8> {
        rows.iter()
            .flat_map(|(key, value)| {
                [
                    &(key.len() as u32).to_le_bytes()[..],
                    key,
                    &(value.len() as u32).to_le_bytes(),
                    value,
                ]
                .concat()
            })
            .collect()
    }

    #[test]
    fn rows_round_trip_skip_foreign_lengths_and_refuse_in_memory_tiers() {
        let entry = CatalogEntry {
            from: StateId::PRE_GENESIS,
            to: StateId::new(0, [0x44; 32]),
            location: SnapshotLocation {
                arena_id: 3,
                offset: 4096,
                size: 333,
            },
            tier: SnapshotTier::PersistedBase,
        };
        let (key, value) = encode_row(&entry);
        assert_eq!(&key[..8], &[0; 8]);
        assert_eq!(
            &key[40..],
            &1i64.to_be_bytes(),
            "a base from PreGenesis spans 1, as the C# ulong wrap"
        );
        assert_eq!(&value[..8], &[0xFF; 8], "PreGenesis is stored as -1");
        assert_eq!(decode_value(&value).unwrap(), entry);

        let packed = pack(&[
            (&key, &value),
            (&key[..47], &value),
            (&key, &value[..100]),
            (&key, &value),
        ]);
        assert_eq!(parse_rows(&packed).unwrap(), [entry, entry]);
        assert!(matches!(parse_rows(&packed[..10]), Err(TierError::InvalidArgument(_))));

        let mut bad = value;
        bad[100] = 1;
        let error = parse_rows(&pack(&[(&key, &bad)])).unwrap_err().to_string();
        assert_eq!(
            error,
            "Persisted snapshot catalog entry has non-persisted tier byte 1 (only Persisted* tiers are ever stored). \
             The persistedSnapshot/ directory has an incompatible or corrupted layout — wipe and resync."
        );
        assert!(matches!(tier_from_byte(6), Err(TierError::InvalidArgument(_))));
    }

    #[test]
    fn blob_range_is_18_little_endian_bytes() {
        let range = BlobRange {
            blob_arena_id: 0x0102,
            offset: 83,
            length: 95,
        };
        let bytes = range.to_bytes();
        assert_eq!(bytes[..4], [0x02, 0x01, 83, 0]);
        assert_eq!(BlobRange::read(&bytes), range);
        assert_eq!(BlobRange::read(&bytes[..17]), BlobRange::NONE);
        assert!(BlobRange::NONE.is_empty());
    }
}
