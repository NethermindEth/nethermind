//! Reading a GolemDB store directory the node has not opened: a store received from a peer by state sync, before it
//! takes the place of the node's own.

use std::path::Path;

use alloy_primitives::Address;
use golemdb_cells::Cells;
use golemdb_merkle::Keccak256Hasher;
use golemdb_storage::Store as _;
use golemdb_storage_mdbx::MdbxStore;

/// The record id of `#recordKeys`, GolemDB's system record binding every record key to its record id
/// (`golemdb_cells::system::RECORD_KEYS`): one cell per record, named by the raw 32-byte key.
const RECORD_KEYS: u64 = 3;

/// Every account the store at `directory` holds. Arkiv keys an account's record by its address left-padded with twelve
/// zero bytes (`arkiv_golemdb_state::accounts::record_key`), a range no entity key can fall in; the binding cells of
/// `#recordKeys` name every live record, so these are all the accounts, read from the committed state.
///
/// The directory must not be open elsewhere in this process (MDBX allows one environment per file per process).
pub fn account_addresses(directory: &Path) -> eyre::Result<Vec<Address>> {
    let store = MdbxStore::open(directory).map_err(|e| eyre::eyre!("open {}: {e}", directory.display()))?;
    let tx = store.begin_read().map_err(|e| eyre::eyre!("read transaction: {e}"))?;
    let hasher = Keccak256Hasher;
    let cells = Cells::new(&hasher);
    let mut addresses = Vec::new();
    for cell in cells.scan_record(&tx, RECORD_KEYS).map_err(|e| eyre::eyre!("scan #recordKeys: {e}"))? {
        let (key, _) = cell.map_err(|e| eyre::eyre!("scan #recordKeys: {e}"))?;
        let name = key.name();
        let raw = name.as_bytes();
        if raw.len() == 32 && raw[..12].iter().all(|b| *b == 0) {
            addresses.push(Address::from_slice(&raw[12..]));
        }
    }
    Ok(addresses)
}
