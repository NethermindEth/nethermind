//! Support shared by the integration tests; each test crate uses a part of it.
#![allow(dead_code)]

pub mod any_kv;
pub mod faulty;
pub mod loopback;
#[cfg(feature = "rocksdb-it")]
pub mod rocks;

use flatdb_core::kv::{Column, IterOptions, KvIterator, KvReader, KvStore};

/// Decodes hex with an optional `0x` prefix, as `Bytes.FromHexString`.
pub fn hex(text: &str) -> Vec<u8> {
    let text = text.strip_prefix("0x").unwrap_or(text);
    assert!(text.len().is_multiple_of(2), "odd hex length: {text}");
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap_or_else(|_| panic!("not hex: {text}")))
        .collect()
}

/// Lowercase hex with a `0x` prefix, as `Bytes.ToHexString(true)`.
pub fn to_hex(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(2 + 2 * bytes.len());
    text.push_str("0x");
    for byte in bytes {
        text.push_str(&format!("{byte:02x}"));
    }
    text
}

pub fn hex32(text: &str) -> [u8; 32] {
    hex(text).try_into().expect("32 bytes")
}

pub fn hex20(text: &str) -> [u8; 20] {
    hex(text).try_into().expect("20 bytes")
}

/// Every entry of `column`, in key order.
pub fn column_entries<K: KvStore>(kv: &K, column: Column) -> Vec<(Vec<u8>, Vec<u8>)> {
    let reader = kv.reader(false).unwrap();
    let mut iter = reader.iter(column, IterOptions::default()).unwrap();
    let mut entries = Vec::new();
    iter.seek_to_first();
    while iter.valid() {
        entries.push((iter.key().to_vec(), iter.value().to_vec()));
        iter.next();
    }
    iter.status().unwrap();
    entries
}

/// The name of a column in `FlatDbColumns`.
pub fn column_name(column: Column) -> &'static str {
    match column {
        Column::Metadata => "Metadata",
        Column::Account => "Account",
        Column::Storage => "Storage",
        Column::StateNodes => "StateNodes",
        Column::StateTopNodes => "StateTopNodes",
        Column::StorageNodes => "StorageNodes",
        Column::FallbackNodes => "FallbackNodes",
    }
}
