//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotUtils.cs`: the check that a converted snapshot
//! reads back as the in-memory snapshot it came from (`ValidatePersistedSnapshot`), and the JSON dump of that snapshot
//! a failed check leaves behind (`DumpSnapshotToJson`).
//!
//! The checks and their messages are the C# ones, but for the order the entries are checked in, which follows the
//! snapshot's maps. The dump has the C# keys and value formats; its entries follow the same maps. A read the table
//! refuses is reported as a mismatch, dump included; C# does so only for the errors it raises as
//! `InvalidOperationException`, not for a blob item longer than its read (`InvalidDataException`).

use std::fmt::Write;
use std::path::Path;

use super::snapshot::PersistedSnapshot;
use super::stack::PersistedLayer;
use super::{TierError, TierResult};
use crate::codec::account::encode_account;
use crate::snapshot::Snapshot;
use crate::types::U256;

/// `ValidatePersistedSnapshot`: every account, slot, self-destruct flag and trie node of `snapshot` must read back from
/// `persisted`. On a mismatch, or a read the table refuses, `snapshot` is dumped to `broken.{from}.{to}.json` in
/// `dump_directory` (C# uses the working directory, an empty path) and the error names the file; an I/O error is
/// returned as it is.
pub fn validate_persisted_snapshot(
    snapshot: &Snapshot,
    persisted: &PersistedSnapshot,
    dump_directory: &Path,
) -> TierResult<()> {
    let message = match find_mismatch(snapshot, persisted) {
        Ok(None) => return Ok(()),
        Ok(Some(message)) => message,
        Err(TierError::Io(error)) => return Err(TierError::Io(error)),
        Err(error) => error.to_string(),
    };
    let file = dump_directory.join(format!(
        "broken.{}.{}.json",
        snapshot.from().block_number,
        snapshot.to().block_number
    ));
    std::fs::write(&file, dump_snapshot_json(snapshot))?;
    Err(TierError::Validation(format!(
        "{message}. Dumped snapshot to {}",
        file.display()
    )))
}

fn find_mismatch(snapshot: &Snapshot, persisted: &PersistedSnapshot) -> TierResult<Option<String>> {
    for (address, expected) in snapshot.accounts() {
        let address_text = hex(address, true);
        let Some(actual) = persisted.try_get_account(address)? else {
            return Ok(Some(format!("Account {address_text} not found in persisted snapshot")));
        };
        match (expected, actual) {
            (None, Some(_)) => {
                return Ok(Some(format!("Account {address_text} should be null but has RLP data")));
            }
            (Some(expected), actual) if actual.as_ref() != Some(expected) => {
                return Ok(Some(format!("Account {address_text} mismatch")));
            }
            _ => {}
        }
    }

    for (key, expected) in snapshot.storages() {
        let slot_text = || format!("{}:{}", hex(&key.address, true), decimal(&key.slot));
        let Some(actual) = persisted.try_get_slot(&key.address, &key.slot)? else {
            return Ok(Some(format!("Storage {} not found in persisted snapshot", slot_text())));
        };
        if actual != *expected {
            return Ok(Some(format!("Storage {} mismatch", slot_text())));
        }
    }

    for (address, expected) in snapshot.self_destructed_storage_addresses() {
        let address_text = hex(address, true);
        let Some(actual) = persisted.try_get_self_destruct_flag(address)? else {
            return Ok(Some(format!(
                "SelfDestruct {address_text} not found in persisted snapshot"
            )));
        };
        if actual != expected {
            return Ok(Some(format!(
                "SelfDestruct {address_text} mismatch: expected {}, got {}",
                csharp_bool(expected),
                csharp_bool(actual)
            )));
        }
    }

    for (path, node) in snapshot.state_nodes() {
        if node.is_hash_only_placeholder() {
            continue;
        }
        let Some(rlp) = persisted.try_load_state_node_rlp(path)? else {
            return Ok(Some(format!(
                "StateNode at path length {} not found in persisted snapshot",
                path.length
            )));
        };
        if rlp != node.rlp {
            return Ok(Some(format!("StateNode at path length {} RLP mismatch", path.length)));
        }
    }

    for (key, node) in snapshot.storage_nodes() {
        if node.is_hash_only_placeholder() {
            continue;
        }
        let node_text = format!(
            "StorageNode {} at path length {}",
            hex(&key.address_hash, true),
            key.path.length
        );
        let Some(rlp) = persisted.try_load_storage_node_rlp(&key.address_hash, &key.path)? else {
            return Ok(Some(format!("{node_text} not found in persisted snapshot")));
        };
        if rlp != node.rlp {
            return Ok(Some(format!("{node_text} RLP mismatch")));
        }
    }
    Ok(None)
}

/// `DumpSnapshotToJson`: one object per kind of entry, keyed and valued as C# writes them, without whitespace.
pub fn dump_snapshot_json(snapshot: &Snapshot) -> String {
    let mut json = JsonObjects::default();
    json.open("accounts");
    for (address, account) in snapshot.accounts() {
        let value = account.map_or_else(String::new, |account| hex(&encode_account(&account, true), false));
        json.string_entry(&hex(address, false), &value);
    }
    json.open("storages");
    for (key, value) in snapshot.storages() {
        let name = format!("{}:{}", hex(&key.address, false), decimal(&key.slot));
        let value = value.map_or_else(String::new, |value| hex(&value.to_be_bytes(), false));
        json.string_entry(&name, &value);
    }
    json.open("selfDestructed");
    for (address, is_new_account) in snapshot.self_destructed_storage_addresses() {
        json.entry(&hex(address, false), if is_new_account { "true" } else { "false" });
    }
    json.open("stateNodes");
    for (path, node) in snapshot.state_nodes() {
        if !node.is_hash_only_placeholder() {
            json.string_entry(
                &format!("{}:{}", hex(&path.path, false), path.length),
                &hex(node.rlp, false),
            );
        }
    }
    json.open("storageNodes");
    for (key, node) in snapshot.storage_nodes() {
        if !node.is_hash_only_placeholder() {
            let name = format!(
                "{}:{}:{}",
                hex(&key.address_hash, false),
                hex(&key.path.path, false),
                key.path.length
            );
            json.string_entry(&name, &hex(node.rlp, false));
        }
    }
    json.finish()
}

/// A JSON object of objects whose names and string values need no escaping: hex, digits and colons.
#[derive(Default)]
struct JsonObjects {
    text: String,
    first_entry: bool,
}

impl JsonObjects {
    fn open(&mut self, name: &str) {
        self.text.push_str(if self.text.is_empty() { "{" } else { "}," });
        write!(self.text, "\"{name}\":{{").expect("writing to a string");
        self.first_entry = true;
    }

    fn entry(&mut self, name: &str, raw_value: &str) {
        if !self.first_entry {
            self.text.push(',');
        }
        self.first_entry = false;
        write!(self.text, "\"{name}\":{raw_value}").expect("writing to a string");
    }

    fn string_entry(&mut self, name: &str, value: &str) {
        self.entry(name, &format!("\"{value}\""));
    }

    fn finish(mut self) -> String {
        self.text.push_str("}}");
        self.text
    }
}

/// `Bytes.ToHexString(withZeroX)`, lowercase; with the prefix it is also how C# prints an `Address` or a `Hash256`.
fn hex(bytes: &[u8], with_zero_x: bool) -> String {
    let mut text = String::with_capacity(2 + 2 * bytes.len());
    if with_zero_x {
        text.push_str("0x");
    }
    for byte in bytes {
        write!(text, "{byte:02x}").expect("writing to a string");
    }
    text
}

/// `UInt256.ToString()`: decimal.
fn decimal(value: &U256) -> String {
    let mut limbs = value.0;
    let mut digits = Vec::new();
    loop {
        let mut remainder = 0u128;
        for limb in limbs.iter_mut().rev() {
            let current = (remainder << 64) | u128::from(*limb);
            *limb = (current / 10) as u64;
            remainder = current % 10;
        }
        digits.push(b'0' + remainder as u8);
        if limbs.iter().all(|&limb| limb == 0) {
            break;
        }
    }
    digits.iter().rev().map(|&digit| char::from(digit)).collect()
}

/// `bool.ToString()`.
fn csharp_bool(value: bool) -> &'static str {
    if value { "True" } else { "False" }
}

#[cfg(test)]
mod tests {
    use rstest::rstest;

    use super::{decimal, dump_snapshot_json};
    use crate::snapshot::Snapshot;
    use crate::snapshot::content::SnapshotContentBuilder;
    use crate::types::{Account, StateId, TreePath, U256};

    #[rstest]
    #[case(U256::ZERO, "0")]
    #[case(U256::from_u64(1_234_567), "1234567")]
    #[case(U256([0, 1, 0, 0]), "18446744073709551616")]
    #[case(
        U256::MAX,
        "115792089237316195423570985008687907853269984665640564039457584007913129639935"
    )]
    fn slots_print_in_decimal(#[case] value: U256, #[case] expected: &str) {
        assert_eq!(decimal(&value), expected);
    }

    #[test]
    fn the_dump_has_the_csharp_shape() {
        let address = [0xAA; 20];
        let mut builder = SnapshotContentBuilder::new();
        builder.set_account(address, Some(Account::new(1, U256::from_u64(2))));
        builder.set_storage(address, U256::from_u64(10), Some(U256::from_u64(0xFF)));
        builder.set_storage(address, U256::from_u64(11), None);
        builder.set_self_destruct(address, true);
        builder.set_state_node(TreePath::from_hex("12"), [0x33; 32], &[0xC0]);
        builder.set_state_node(TreePath::from_hex("34"), [0x44; 32], &[]);
        builder.set_storage_node([0x55; 32], TreePath::from_hex("5"), [0x66; 32], &[0xC1, 0x80]);
        let snapshot = Snapshot::new(StateId::new(1, [1; 32]), StateId::new(2, [2; 32]), builder.build());

        let json: serde_json::Value = serde_json::from_str(&dump_snapshot_json(&snapshot)).unwrap();
        let address_hex = "aa".repeat(20);
        let path = |prefix: &str| format!("{prefix:0<64}");
        assert_eq!(
            json,
            serde_json::json!({
                "accounts": { address_hex.clone(): "c401028080" },
                "storages": {
                    format!("{address_hex}:10"): format!("{:0>64}", "ff"),
                    format!("{address_hex}:11"): "",
                },
                "selfDestructed": { address_hex: true },
                "stateNodes": { format!("{}:2", path("12")): "c0" },
                "storageNodes": { format!("{}:{}:1", "55".repeat(32), path("5")): "c180" },
            })
        );
    }
}
