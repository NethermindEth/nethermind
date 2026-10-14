//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/Block.cs`: the self-describing block of front-coded
//! records both the data blocks and the index of a table are made of.
//!
//! ```text
//! [formatFlag u8]                     data block: W = 2, index: W = 4
//! [recordsEnd : W]                    block-relative end of the records
//! [numRestarts : W]
//! [restartOffset : W × numRestarts]   block-relative starts of the cp == 0 records
//! data record    [cp u8][suffixLen u8][valueLen u8][keySuffix][value]
//! index record   [cp u8][suffixLen u8][valChangedLen u8][keySuffix][valChanged]
//! ```
//! A record with `cp == 0` stores its full key and is a restart: the builder forces one every restart interval, and one
//! arises wherever a key shares no leading byte with its predecessor. An index value is a data block's offset, stored
//! as only the low little-endian bytes that changed from the previous record's value, restated in full at a restart.

/// The flag of a data block, whose offsets take 2 bytes.
pub const FLAG_BLOCK: u8 = 1;
/// The flag of the index block, whose offsets take 4 bytes.
pub const FLAG_INDEX: u8 = 2;

/// The fixed prefix of every record: common-prefix length, suffix length and value (or changed-value) length.
pub const RECORD_HEADER_LENGTH: usize = 3;

/// `WidthFromFlag`: the offset width of a block kind, 0 for an unknown flag.
pub const fn width_from_flag(flag: u8) -> usize {
    match flag {
        FLAG_BLOCK => 2,
        FLAG_INDEX => 4,
        _ => 0,
    }
}

/// `RecordsStart`: the block-relative offset of the first record.
pub const fn records_start(width: usize, restart_count: usize) -> usize {
    1 + 2 * width + width * restart_count
}
