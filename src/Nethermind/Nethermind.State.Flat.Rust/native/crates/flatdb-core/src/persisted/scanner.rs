//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotScanner.cs`: full forward passes over a table,
//! one per view: the per-address groups (account, self-destruct flag, slots), the state nodes and the storage nodes.
//!
//! Each view walks the table from the start and skips the columns it does not own. A per-address group streams its
//! slots off the pass's own cursor ([`PerAddressScan::next_slot`]), so an account with any number of slots costs no
//! buffer; slots the caller leaves undrained are skipped by the next [`PerAddressScan::next_address`].

use super::io::ByteReader;
use super::keys::{
    self, ACCOUNT_COLUMN, ACCOUNT_DELETED_MARKER, ACCOUNT_SUB, SELF_DESTRUCT_DESTRUCTED_MARKER, SELF_DESTRUCT_SUB,
    SLOT_SUB, STATE_FALLBACK_COLUMN, STATE_TOP_COLUMN, STORAGE_COLUMN,
};
use super::reader::{decode_slot, read_value};
use super::sorted::enumerator::SortedTableEnumerator;
use super::{TierError, TierResult};
use crate::codec::account::decode_account;
use crate::types::{Account, Address, Hash256, TreePath, U256};

/// The scans of one table.
pub struct Scanner<'r, 'a> {
    reader: &'r ByteReader<'a>,
}

impl<'r, 'a> Scanner<'r, 'a> {
    pub fn new(reader: &'r ByteReader<'a>) -> Self {
        Self { reader }
    }

    /// `PerAddresses`.
    pub fn per_addresses(&self) -> PerAddressScan<'r, 'a> {
        PerAddressScan::new(self.reader)
    }

    /// `StateNodes`.
    pub fn state_nodes(&self) -> NodeScan<'r, 'a> {
        NodeScan::new(self.reader, NodeKind::State)
    }

    /// `StorageNodes`.
    pub fn storage_nodes(&self) -> NodeScan<'r, 'a> {
        NodeScan::new(self.reader, NodeKind::Storage)
    }
}

/// `PerAddressEntry`: an address of the table, with its self-destruct flag (`Some(false)` destructed, `Some(true)` new
/// account) and whether it has an account record, which [`PerAddressScan::account`] decodes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PerAddressEntry {
    pub address: Address,
    pub has_account: bool,
    pub self_destruct_flag: Option<bool>,
}

/// `SlotEntry`: a slot, big-endian, and its value; `None` for a deleted slot.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SlotEntry {
    pub slot: [u8; 32],
    pub value: Option<U256>,
}

impl SlotEntry {
    pub fn slot_index(&self) -> U256 {
        U256::from_be_bytes(&self.slot)
    }
}

/// `PerAddressEnumerator` with its `SlotEnumerator`.
pub struct PerAddressScan<'r, 'a> {
    reader: &'r ByteReader<'a>,
    inner: SortedTableEnumerator,
    has_row: bool,
    started: bool,
    current: Option<Group>,
}

struct Group {
    address: Address,
    account: Option<(usize, usize)>,
    /// Whether the cursor is parked on, or streaming, this group's slots.
    slots_active: bool,
    /// Whether the first slot, on which the cursor was parked, has been yielded.
    slots_started: bool,
}

impl<'r, 'a> PerAddressScan<'r, 'a> {
    fn new(reader: &'r ByteReader<'a>) -> Self {
        Self {
            reader,
            inner: SortedTableEnumerator::new(reader),
            has_row: false,
            started: false,
            current: None,
        }
    }

    /// `MoveNext`: the next address, after skipping what is left of the previous one's slots.
    pub fn next_address(&mut self) -> TierResult<Option<PerAddressEntry>> {
        if !self.started {
            self.started = true;
            self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
        }
        if let Some(group) = &self.current {
            let address = group.address;
            while self.is_current_sub(&address, SLOT_SUB) {
                self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
            }
        }

        // Skip to the per-address column; metadata sorts after it.
        while self.has_row && self.inner.current_key()[0] != ACCOUNT_COLUMN {
            if self.inner.current_key()[0] > ACCOUNT_COLUMN {
                self.has_row = false;
                break;
            }
            self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
        }
        if !self.has_row {
            self.current = None;
            return Ok(None);
        }

        let address: Address = keys::per_address_address(self.inner.current_key())
            .try_into()
            .expect("20 bytes");
        let mut account = None;
        let mut self_destruct_flag = None;
        if self.is_current_sub(&address, ACCOUNT_SUB) {
            account = Some(self.inner.current_value());
            self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
        }
        if self.is_current_sub(&address, SELF_DESTRUCT_SUB) {
            // As C#, the flag byte is read at the value's offset whatever its length; a failed read is "destructed".
            let flag = self.reader.read_u8(self.inner.current_value().0).unwrap_or(0);
            self_destruct_flag = Some(flag != SELF_DESTRUCT_DESTRUCTED_MARKER);
            self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
        }

        let slots_active = self.is_current_sub(&address, SLOT_SUB);
        self.current = Some(Group {
            address,
            account,
            slots_active,
            slots_started: false,
        });
        Ok(Some(PerAddressEntry {
            address,
            has_account: account.is_some(),
            self_destruct_flag,
        }))
    }

    /// `Account`: the current address's account, `None` for no account record or a deleted one.
    pub fn account(&self) -> TierResult<Option<Account>> {
        let Some((offset, length)) = self.current.as_ref().and_then(|group| group.account) else {
            return Ok(None);
        };
        let rlp = read_value(self.reader, offset, length)?;
        if rlp == [ACCOUNT_DELETED_MARKER] {
            return Ok(None);
        }
        decode_account(rlp, true).map_err(|error| TierError::Corrupt(error.to_string()))
    }

    /// `SlotEnumerator.MoveNext`: the current address's next slot.
    pub fn next_slot(&mut self) -> TierResult<Option<SlotEntry>> {
        let Some(group) = &mut self.current else {
            return Ok(None);
        };
        if !group.slots_active {
            return Ok(None);
        }
        if group.slots_started {
            if !keys::move_next_checked(&mut self.inner, self.reader)? {
                self.has_row = false;
                group.slots_active = false;
                return Ok(None);
            }
        } else {
            group.slots_started = true;
        }

        let key = self.inner.current_key();
        let is_slot_of_group = key[0] == ACCOUNT_COLUMN
            && keys::per_address_sub_column(key) == SLOT_SUB
            && keys::per_address_address(key) == group.address;
        if !is_slot_of_group {
            group.slots_active = false;
            return Ok(None);
        }

        let slot: [u8; 32] = keys::slot_key_bytes(key).try_into().expect("32 bytes");
        let (offset, length) = self.inner.current_value();
        Ok(Some(SlotEntry {
            slot,
            value: decode_slot(self.reader, offset, length)?,
        }))
    }

    /// `IsCurrentSub`: whether the cursor is on `address`'s group at sub-column `sub`.
    fn is_current_sub(&self, address: &Address, sub: u8) -> bool {
        if !self.has_row {
            return false;
        }
        let key = self.inner.current_key();
        key[0] == ACCOUNT_COLUMN
            && keys::per_address_sub_column(key) == sub
            && keys::per_address_address(key) == address
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum NodeKind {
    State,
    Storage,
}

/// A trie node of the table: its path, the account's address hash (the 20 stored bytes, zero-padded) for a storage
/// node, and the bytes of its [`super::page_layout::NodeRef`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NodeEntry<'a> {
    pub address_hash: Hash256,
    pub path: TreePath,
    pub node_ref: &'a [u8],
}

/// `StateNodeEnumerator` / `StorageNodeEnumerator`.
pub struct NodeScan<'r, 'a> {
    reader: &'r ByteReader<'a>,
    kind: NodeKind,
    inner: SortedTableEnumerator,
    has_row: bool,
    started: bool,
}

impl<'r, 'a> NodeScan<'r, 'a> {
    fn new(reader: &'r ByteReader<'a>, kind: NodeKind) -> Self {
        Self {
            reader,
            kind,
            inner: SortedTableEnumerator::new(reader),
            has_row: false,
            started: false,
        }
    }

    pub fn next_node(&mut self) -> TierResult<Option<NodeEntry<'a>>> {
        // The first call reads the first row; each later one moves past the row it returned.
        self.has_row = if self.started {
            self.has_row && keys::move_next_checked(&mut self.inner, self.reader)?
        } else {
            self.started = true;
            keys::move_next_checked(&mut self.inner, self.reader)?
        };

        // State columns (FB..=FD) follow the storage column (FA): past its column, a view has nothing more.
        let (first, last) = match self.kind {
            NodeKind::State => (STATE_FALLBACK_COLUMN, STATE_TOP_COLUMN),
            NodeKind::Storage => (STORAGE_COLUMN, STORAGE_COLUMN),
        };
        while self.has_row {
            let column = self.inner.current_key()[0];
            if (first..=last).contains(&column) {
                return self.entry().map(Some);
            }
            if column > last {
                self.has_row = false;
                break;
            }
            self.has_row = keys::move_next_checked(&mut self.inner, self.reader)?;
        }
        Ok(None)
    }

    fn entry(&self) -> TierResult<NodeEntry<'a>> {
        let key = self.inner.current_key();
        let (offset, length) = self.inner.current_value();
        let node_ref = read_value(self.reader, offset, length)?;
        Ok(match self.kind {
            NodeKind::State => NodeEntry {
                address_hash: [0; 32],
                path: keys::decode_path(keys::state_path_bytes(key), keys::state_stage(key[0])),
                node_ref,
            },
            NodeKind::Storage => {
                let mut address_hash = [0u8; 32];
                address_hash[..keys::ADDRESS_HASH_PREFIX_LENGTH].copy_from_slice(keys::storage_address_hash(key));
                NodeEntry {
                    address_hash,
                    path: keys::decode_path(
                        keys::storage_path_bytes(key),
                        keys::storage_stage(keys::storage_sub_column(key)),
                    ),
                    node_ref,
                }
            }
        })
    }
}
