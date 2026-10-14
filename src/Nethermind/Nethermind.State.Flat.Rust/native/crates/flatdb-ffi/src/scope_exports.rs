//! The exports of the read-only bundles (`fdb_rob_*`), the scope bundles (`fdb_scope_*`) and the snapshots a scope
//! commits, which `fdb_add_snapshot` adds to the manager.
//!
//! A scope begins with the read-only bundle it was opened over ([`FdbScope::base`]), so every `fdb_rob_*` export also
//! takes a scope: it then reads that bundle alone, its in-memory snapshots and the persistence, as the base view of the
//! C# `ReadOnlySnapshotBundle` over a scope does. Reads report where they were served from as a
//! [`flatdb_core::bundle::ReadSource`] byte.

use std::slice;
use std::sync::Arc;

use flatdb_core::bundle::ReadSource;
use flatdb_core::kv::{GetInto, ReadFlags};
use flatdb_core::manager::AddSnapshotOutcome;
use flatdb_core::persistence::ReaderFlags;
use flatdb_core::scope_bundle::{ScopeBundle, ScopeFlags};
use flatdb_core::snapshot::Snapshot;
#[cfg(any(test, feature = "test-api"))]
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::mutable::NodeRecord;
use flatdb_core::stats::FlatStats;
use flatdb_core::types::{Account, StateId};

use crate::abi::{FdbStatus, FfiAccount, FfiError, FfiNodeRecord, FfiRobInfo, FfiScopeFlags, FfiStateId};
use crate::calls::Export;
use crate::guard::{Poison, ffi_guard};
use crate::manager::{Bundle, FdbManager, refused};
use crate::manager_exports::{mgr_ref, state};
use crate::persistence::ErasedReader;
use crate::{array, bytes, bytes_mut, out, read_u256, report_len, tree_path, write_found_u256};

pub type Scope = ScopeBundle<ErasedReader>;

/// A read-only bundle, handed to C# as an opaque pointer: the in-memory snapshots a gather found above the persisted
/// state, over a reader of it.
#[repr(C)]
#[derive(Clone)]
pub struct FdbRob {
    pub bundle: Arc<Bundle>,
    /// The store's poison flag, which every call checks, and a handle count that keeps the store alive.
    pub poison: Arc<Poison>,
    /// The metrics of the manager the bundle was gathered from, which the contents of scopes over it count in.
    pub stats: Arc<FlatStats>,
    pub info: FfiRobInfo,
}

impl FdbRob {
    fn new(bundle: Arc<Bundle>, poison: &Arc<Poison>, stats: &Arc<FlatStats>) -> Self {
        let info = FfiRobInfo {
            reader_state: bundle.reader_state().into(),
            snapshot_count: bundle.snapshot_count() as u32,
            may_filter_slots: bundle.may_filter_slots().into(),
            has_self_destructs: bundle.has_self_destructs().into(),
            ..FfiRobInfo::default()
        };
        Self {
            bundle,
            poison: Arc::clone(poison),
            stats: Arc::clone(stats),
            info,
        }
    }

    /// The bundle under a scope over it, seeded with `own`.
    fn with_scope(self, own: Vec<Arc<Snapshot>>, flags: ScopeFlags) -> FdbScope {
        let scope = ScopeBundle::new(Arc::clone(&self.bundle), own, flags, Arc::clone(&self.stats));
        let info = FfiRobInfo {
            own_count: scope.own_count() as u32,
            has_self_destructs: scope.may_have_self_destructs().into(),
            ..self.info
        };
        FdbScope {
            base: FdbRob { info, ..self },
            scope,
        }
    }
}

/// A scope bundle, handed to C# as an opaque pointer. Its first field is the bundle it reads below its own snapshots,
/// so a pointer to it is also a valid [`FdbRob`] pointer: the scope's base view.
#[repr(C)]
pub struct FdbScope {
    pub base: FdbRob,
    pub scope: Scope,
}

/// A snapshot a scope committed, handed to C# as an opaque pointer; it needs neither the store nor the manager.
pub type FdbSnapshot = Arc<Snapshot>;

/// `GatherReadOnlySnapshotBundle(base, readerFlags)`: a read-only bundle of the state `base`, read with the `fdb_rob_*`
/// exports and released with [`fdb_rob_release`]; `PreGenesis` reads an empty state. `info` describes it.
/// [`FdbStatus::StateNotRetained`] when no snapshot ends at the state and it is not the persisted one,
/// [`FdbStatus::GatherTimeout`] when a race with persistence outlasts the retries.
///
/// # Safety
/// `mgr` must be a live manager, `base` valid and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_gather(
    mgr: *const FdbManager,
    base: *const FfiStateId,
    reader_flags: u32,
    rob: *mut *mut FdbRob,
    info: *mut FfiRobInfo,
) -> i32 {
    ffi_guard(Export::RobGather, || {
        // SAFETY: the caller passes a live manager and valid pointers.
        let (mgr, base, rob, info) = unsafe { (mgr_ref(mgr)?, state(base)?, out(rob)?, out(info)?) };
        let gathered = gather(mgr, base, reader_flags)?;
        *info = gathered.info;
        *rob = Box::into_raw(Box::new(gathered));
        Ok(())
    })
}

/// Releases a read-only bundle.
///
/// # Safety
/// `rob` must be null or a bundle from [`fdb_rob_gather`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_release(rob: *mut FdbRob) -> i32 {
    ffi_guard(Export::RobRelease, || {
        if !rob.is_null() {
            // SAFETY: the caller hands back ownership of a bundle from `fdb_rob_gather`.
            drop(unsafe { Box::from_raw(rob) });
        }
        Ok(())
    })
}

/// `ReadOnlySnapshotBundle.GetAccount`: stores whether the account exists in `found`, the account in `account` when it
/// does, and where it was read in `source`.
///
/// # Safety
/// `rob` must be a live bundle or scope, `address` valid for 20 bytes and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_get_account(
    rob: *const FdbRob,
    address: *const u8,
    account: *mut FfiAccount,
    found: *mut u8,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::RobGetAccount, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, address, account, found, source) = unsafe {
            (
                rob_ref(rob)?,
                array::<20>(address)?,
                out(account)?,
                out(found)?,
                out(source)?,
            )
        };
        let read = rob.poison.guard(|| Ok(rob.bundle.get_account(address)?))?;
        write_account(read, account, found, source);
        Ok(())
    })
}

/// `ReadOnlySnapshotBundle.DetermineSelfDestructSnapshotIdx`: the index of the newest snapshot that cleared the storage
/// of `address`, or -1, in `idx`.
///
/// # Safety
/// `rob` must be a live bundle or scope, `address` valid for 20 bytes and `idx` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_self_destruct_idx(rob: *const FdbRob, address: *const u8, idx: *mut i32) -> i32 {
    ffi_guard(Export::RobSelfDestructIdx, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, address, idx) = unsafe { (rob_ref(rob)?, array::<20>(address)?, out(idx)?) };
        *idx = rob
            .poison
            .guard(|| Ok(rob.bundle.determine_self_destruct_snapshot_idx(address)?))?;
        Ok(())
    })
}

/// `ReadOnlySnapshotBundle.GetSlot` at the caller's self-destruct index `sd_idx` (-1 for none), or `GetSlotFiltered`
/// when `filtered` is 1: stores whether the slot has a value in `found`, its `UInt256` limbs in `value` when it does,
/// and where it was read in `source`.
///
/// # Safety
/// `rob` must be a live bundle or scope, `address` valid for 20 bytes, `slot` for four limbs, `value` for writes of
/// four limbs and the other out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_get_slot(
    rob: *const FdbRob,
    address: *const u8,
    slot: *const u64,
    sd_idx: i32,
    filtered: u8,
    value: *mut u64,
    found: *mut u8,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::RobGetSlot, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, address, slot, found, source) = unsafe {
            (
                rob_ref(rob)?,
                array::<20>(address)?,
                read_u256(slot)?,
                out(found)?,
                out(source)?,
            )
        };
        let (read, from) = rob.poison.guard(|| {
            Ok(if filtered != 0 {
                rob.bundle.get_slot_filtered(address, &slot, sd_idx)?
            } else {
                rob.bundle.get_slot(address, &slot, sd_idx)?
            })
        })?;
        *source = from as u8;
        // SAFETY: as above.
        unsafe { write_found_u256(read, value, found) }
    })
}

/// The read-only adapter's `TryLoadRlp` of the state trie node at `path`: the newest in-memory node at the path,
/// whatever its hash (`hash` is not compared), or the persisted one when there is none or it is a hash-only
/// placeholder. Copies the RLP into `buffer`, its length into `len` (-1 when there is no node) and where it was read
/// into `source`; an RLP longer than `capacity` yields [`FdbStatus::BufferTooSmall`] with its length in `len`.
///
/// # Safety
/// `rob` must be a live bundle or scope, `path` and `hash` valid for 32 bytes, `buffer` for writes of `capacity` bytes
/// and the other out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_load_state_rlp(
    rob: *const FdbRob,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::RobLoadStateRlp, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, path, _, buffer, len, source) = unsafe {
            (
                rob_ref(rob)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
                out(source)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let (read, from) = rob
            .poison
            .guard(|| Ok(rob.bundle.load_state_rlp_unchecked(&path, flags, copy_into(buffer))?))?;
        *source = from as u8;
        report_len(read.unwrap_or(GetInto::NotFound), capacity, len)
    })
}

/// As [`fdb_rob_load_state_rlp`], in the storage trie of the account hashing to `address_hash`.
///
/// # Safety
/// As [`fdb_rob_load_state_rlp`], and `address_hash` valid for 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rob_load_storage_rlp(
    rob: *const FdbRob,
    address_hash: *const u8,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::RobLoadStorageRlp, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, address_hash, path, _, buffer, len, source) = unsafe {
            (
                rob_ref(rob)?,
                array::<32>(address_hash)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
                out(source)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let (read, from) = rob.poison.guard(|| {
            Ok(rob
                .bundle
                .load_storage_rlp_unchecked(address_hash, &path, flags, copy_into(buffer))?)
        })?;
        *source = from as u8;
        report_len(read.unwrap_or(GetInto::NotFound), capacity, len)
    })
}

/// [`fdb_rob_gather`] and [`fdb_scope_create`] in one call: a scope of the state `base` with no own snapshots, whose
/// base view `info` describes. Fails as [`fdb_rob_gather`] does.
///
/// # Safety
/// `mgr` must be a live manager, `base` and `flags` valid and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_open(
    mgr: *const FdbManager,
    base: *const FfiStateId,
    reader_flags: u32,
    flags: *const FfiScopeFlags,
    scope: *mut *mut FdbScope,
    info: *mut FfiRobInfo,
) -> i32 {
    ffi_guard(Export::ScopeOpen, || {
        // SAFETY: the caller passes a live manager and valid pointers.
        let (mgr, base, flags, scope, info) =
            unsafe { (mgr_ref(mgr)?, state(base)?, flags.as_ref(), out(scope)?, out(info)?) };
        let flags = ScopeFlags::try_from(flags.ok_or_else(|| FfiError::invalid_argument("null flags"))?)?;
        let opened = gather(mgr, base, reader_flags)?.with_scope(Vec::new(), flags);
        *info = opened.base.info;
        *scope = Box::into_raw(Box::new(opened));
        Ok(())
    })
}

/// `SnapshotBundle`'s constructor: a scope over the bundle (or the base view of the scope) `rob`, whose own snapshots
/// start with the `own_count` snapshots of `own`, oldest first, which the scope shares with the caller. `info`
/// describes its base view, with the own snapshots counted and their clears included.
///
/// # Safety
/// `rob` must be a live bundle or scope, `own` valid for reads of `own_count` live snapshots unless `own_count` is 0,
/// `flags` valid and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_create(
    rob: *const FdbRob,
    own: *const *const FdbSnapshot,
    own_count: u32,
    flags: *const FfiScopeFlags,
    scope: *mut *mut FdbScope,
    info: *mut FfiRobInfo,
) -> i32 {
    ffi_guard(Export::ScopeCreate, || {
        // SAFETY: the caller passes a live bundle and valid pointers.
        let (rob, flags, scope, info) = unsafe { (rob_ref(rob)?, flags.as_ref(), out(scope)?, out(info)?) };
        let flags = ScopeFlags::try_from(flags.ok_or_else(|| FfiError::invalid_argument("null flags"))?)?;
        let own = match own_count {
            0 => Vec::new(),
            _ if own.is_null() => return Err(FfiError::invalid_argument("null own snapshots")),
            // SAFETY: the caller guarantees `own_count` snapshot pointers.
            _ => unsafe { slice::from_raw_parts(own, own_count as usize) }
                .iter()
                // SAFETY: the caller guarantees live snapshots.
                .map(|snapshot| unsafe { snapshot.as_ref() }.map(Arc::clone))
                .collect::<Option<Vec<_>>>()
                .ok_or_else(|| FfiError::invalid_argument("null own snapshot"))?,
        };
        let created = rob.poison.guard(|| Ok(rob.clone().with_scope(own, flags)))?;
        *info = created.base.info;
        *scope = Box::into_raw(Box::new(created));
        Ok(())
    })
}

/// Releases a scope; the snapshots it committed and was seeded with stay alive while anything else holds them.
///
/// # Safety
/// `scope` must be null or a scope from [`fdb_scope_open`] or [`fdb_scope_create`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_release(scope: *mut FdbScope) -> i32 {
    ffi_guard(Export::ScopeRelease, || {
        if !scope.is_null() {
            // SAFETY: the caller hands back ownership of a scope.
            drop(unsafe { Box::from_raw(scope) });
        }
        Ok(())
    })
}

/// `SnapshotBundle.GetAccount` through the current content, the own snapshots and the bundle, reporting as
/// [`fdb_rob_get_account`]. With `promote` 1, an account read below the current content is cached in it, as
/// `FlatWorldStateScope.Get` promotes one.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_get_account(
    scope: *const FdbScope,
    address: *const u8,
    promote: u8,
    account: *mut FfiAccount,
    found: *mut u8,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::ScopeGetAccount, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, account, found, source) = unsafe {
            (
                scope_ref(scope)?,
                array::<20>(address)?,
                out(account)?,
                out(found)?,
                out(source)?,
            )
        };
        let read = scope
            .base
            .poison
            .guard(|| Ok(scope.scope.get_account(address, promote != 0)?))?;
        write_account(read, account, found, source);
        Ok(())
    })
}

/// `SnapshotBundle.PromoteAccount`: caches `account` (null for an absent one) in the current content unless it holds
/// the address already.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and `account` null or valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_promote_account(
    scope: *const FdbScope,
    address: *const u8,
    account: *const FfiAccount,
) -> i32 {
    ffi_guard(Export::ScopePromoteAccount, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, account) = unsafe { (scope_ref(scope)?, array::<20>(address)?, account.as_ref()) };
        scope.base.poison.guard(|| {
            scope.scope.promote_account(address, account.map(Account::from));
            Ok(())
        })
    })
}

/// `SnapshotBundle.SetAccount`; a null `account` deletes the account.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and `account` null or valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_set_account(
    scope: *const FdbScope,
    address: *const u8,
    account: *const FfiAccount,
) -> i32 {
    ffi_guard(Export::ScopeSetAccount, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, account) = unsafe { (scope_ref(scope)?, array::<20>(address)?, account.as_ref()) };
        scope.base.poison.guard(|| {
            scope.scope.set_account(address, account.map(Account::from));
            Ok(())
        })
    })
}

/// `SnapshotBundle.DetermineSelfDestructSnapshotIdx`: the index of the newest layer that cleared the storage of
/// `address`, or -1, in `idx`: the bundle's snapshots count from 0, the own snapshots after them, then the current
/// content.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and `idx` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_self_destruct_idx(scope: *const FdbScope, address: *const u8, idx: *mut i32) -> i32 {
    ffi_guard(Export::ScopeSelfDestructIdx, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, idx) = unsafe { (scope_ref(scope)?, array::<20>(address)?, out(idx)?) };
        *idx = scope
            .base
            .poison
            .guard(|| Ok(scope.scope.self_destruct_idx(address)?))?;
        Ok(())
    })
}

/// `SnapshotBundle.GetSlot` at the caller's self-destruct index `sd_idx` (-1 for none), reporting as
/// [`fdb_rob_get_slot`]; the scope's flags decide whether the bundle's slot filter is used.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes, `slot` for four limbs, `value` for writes of four limbs
/// and the other out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_get_slot(
    scope: *const FdbScope,
    address: *const u8,
    slot: *const u64,
    sd_idx: i32,
    value: *mut u64,
    found: *mut u8,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::ScopeGetSlot, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, slot, found, source) = unsafe {
            (
                scope_ref(scope)?,
                array::<20>(address)?,
                read_u256(slot)?,
                out(found)?,
                out(source)?,
            )
        };
        let (read, from) = scope
            .base
            .poison
            .guard(|| Ok(scope.scope.get_slot(address, &slot, sd_idx)?))?;
        *source = from as u8;
        // SAFETY: as above.
        unsafe { write_found_u256(read, value, found) }
    })
}

/// `SnapshotBundle.SetChangedSlot`: a zero `value` deletes the slot.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and `slot` and `value` for four limbs.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_set_slot(
    scope: *const FdbScope,
    address: *const u8,
    slot: *const u64,
    value: *const u64,
) -> i32 {
    ffi_guard(Export::ScopeSetSlot, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, slot, value) = unsafe {
            (
                scope_ref(scope)?,
                array::<20>(address)?,
                read_u256(slot)?,
                read_u256(value)?,
            )
        };
        scope.base.poison.guard(|| {
            scope.scope.set_slot(address, &slot, &value);
            Ok(())
        })
    })
}

/// `SnapshotBundle.ClearStorage` of the account at `address`, whose path is `address_hash`.
///
/// # Safety
/// `scope` must be a live scope, `address` valid for 20 bytes and `address_hash` for 32.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_clear_storage(
    scope: *const FdbScope,
    address: *const u8,
    address_hash: *const u8,
) -> i32 {
    ffi_guard(Export::ScopeClearStorage, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address, address_hash) =
            unsafe { (scope_ref(scope)?, array::<20>(address)?, array::<32>(address_hash)?) };
        scope
            .base
            .poison
            .guard(|| Ok(scope.scope.clear_storage(address, address_hash)?))
    })
}

/// The scope adapter's load of the state trie node at `path` with `hash`, reporting as [`fdb_rob_load_state_rlp`]. The
/// first in-memory node at the path, in the current content, the own snapshots, then the bundle's snapshots, must have
/// `hash` (a zero hash for an inline node), or the call fails with [`FdbStatus::HashMismatch`]; a hash-only placeholder
/// of that hash reads the persisted node.
///
/// # Safety
/// As [`fdb_rob_load_state_rlp`], with a live scope.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_load_state_rlp(
    scope: *const FdbScope,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::ScopeLoadStateRlp, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, path, hash, buffer, len, source) = unsafe {
            (
                scope_ref(scope)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
                out(source)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let (read, from) = scope
            .base
            .poison
            .guard(|| Ok(scope.scope.load_state_rlp(&path, hash, flags, copy_into(buffer))?))?;
        *source = from as u8;
        report_len(read.unwrap_or(GetInto::NotFound), capacity, len)
    })
}

/// As [`fdb_scope_load_state_rlp`], in the storage trie of the account hashing to `address_hash`.
///
/// # Safety
/// As [`fdb_scope_load_state_rlp`], and `address_hash` valid for 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_load_storage_rlp(
    scope: *const FdbScope,
    address_hash: *const u8,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
    source: *mut u8,
) -> i32 {
    ffi_guard(Export::ScopeLoadStorageRlp, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address_hash, path, hash, buffer, len, source) = unsafe {
            (
                scope_ref(scope)?,
                array::<32>(address_hash)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
                out(source)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let (read, from) = scope.base.poison.guard(|| {
            Ok(scope
                .scope
                .load_storage_rlp(address_hash, &path, hash, flags, copy_into(buffer))?)
        })?;
        *source = from as u8;
        report_len(read.unwrap_or(GetInto::NotFound), capacity, len)
    })
}

/// `SnapshotBundle.SetStateNode`: the node at `path` with `hash` (zero for an inline node) and `rlp_len` bytes of RLP,
/// 0 for a hash-only placeholder.
///
/// # Safety
/// `scope` must be a live scope, `path` and `hash` valid for 32 bytes and `rlp` for `rlp_len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_set_state_node(
    scope: *const FdbScope,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    rlp: *const u8,
    rlp_len: u32,
) -> i32 {
    ffi_guard(Export::ScopeSetStateNode, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, path, hash, rlp) = unsafe {
            (
                scope_ref(scope)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes(rlp, rlp_len as usize)?,
            )
        };
        scope.base.poison.guard(|| {
            scope.scope.set_state_node(&path, hash, rlp);
            Ok(())
        })
    })
}

/// `SnapshotBundle.SetStorageNode`, in the storage trie of the account hashing to `address_hash`, as
/// [`fdb_scope_set_state_node`].
///
/// # Safety
/// As [`fdb_scope_set_state_node`], and `address_hash` valid for 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_set_storage_node(
    scope: *const FdbScope,
    address_hash: *const u8,
    path: *const u8,
    path_len: u8,
    hash: *const u8,
    rlp: *const u8,
    rlp_len: u32,
) -> i32 {
    ffi_guard(Export::ScopeSetStorageNode, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address_hash, path, hash, rlp) = unsafe {
            (
                scope_ref(scope)?,
                array::<32>(address_hash)?,
                tree_path(path, path_len)?,
                array::<32>(hash)?,
                bytes(rlp, rlp_len as usize)?,
            )
        };
        scope.base.poison.guard(|| {
            scope.scope.set_storage_node(address_hash, &path, hash, rlp);
            Ok(())
        })
    })
}

/// `SnapshotBundle.PublishStateNodes`: sets the `count` nodes of `records`, in order, whose RLP lies in the `blob_len`
/// bytes of `blob`. A record outside the blob fails the call before any node is set.
///
/// # Safety
/// `scope` must be a live scope, `records` valid for reads of `count` records and `blob` for `blob_len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_put_state_nodes(
    scope: *const FdbScope,
    records: *const FfiNodeRecord,
    count: u32,
    blob: *const u8,
    blob_len: usize,
) -> i32 {
    ffi_guard(Export::ScopePutStateNodes, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, records, blob) =
            unsafe { (scope_ref(scope)?, node_records(records, count)?, bytes(blob, blob_len)?) };
        let records = to_node_records(records, blob.len())?;
        scope.base.poison.guard(|| {
            scope.scope.put_state_nodes(&records, blob);
            Ok(())
        })
    })
}

/// `SnapshotBundle.PublishStorageNodes`, in the storage trie of the account hashing to `address_hash`, as
/// [`fdb_scope_put_state_nodes`].
///
/// # Safety
/// As [`fdb_scope_put_state_nodes`], and `address_hash` valid for 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_put_storage_nodes(
    scope: *const FdbScope,
    address_hash: *const u8,
    records: *const FfiNodeRecord,
    count: u32,
    blob: *const u8,
    blob_len: usize,
) -> i32 {
    ffi_guard(Export::ScopePutStorageNodes, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, address_hash, records, blob) = unsafe {
            (
                scope_ref(scope)?,
                array::<32>(address_hash)?,
                node_records(records, count)?,
                bytes(blob, blob_len)?,
            )
        };
        let records = to_node_records(records, blob.len())?;
        scope.base.poison.guard(|| {
            scope.scope.put_storage_nodes(address_hash, &records, blob);
            Ok(())
        })
    })
}

/// The content part of `SnapshotBundle.CollectAndApplySnapshot`: freezes the current content into the snapshot from
/// `from` to `to` and appends it to the own snapshots, kept or not. With `keep` 1, stores the snapshot in `snapshot`
/// for [`fdb_add_snapshot`], released with [`fdb_snapshot_release`]; null otherwise.
///
/// # Safety
/// `scope` must be a live scope no other thread writes during the call, `from` and `to` valid and `snapshot` valid for
/// writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_scope_commit(
    scope: *const FdbScope,
    from: *const FfiStateId,
    to: *const FfiStateId,
    keep: u8,
    snapshot: *mut *mut FdbSnapshot,
) -> i32 {
    ffi_guard(Export::ScopeCommit, || {
        // SAFETY: the caller passes a live scope and valid pointers.
        let (scope, from, to, snapshot) = unsafe { (scope_ref(scope)?, state(from)?, state(to)?, out(snapshot)?) };
        let kept = scope
            .base
            .poison
            .guard(|| Ok(scope.scope.commit(from, to, keep != 0)))?;
        *snapshot = kept.map_or(std::ptr::null_mut(), |kept| Box::into_raw(Box::new(kept)));
        Ok(())
    })
}

/// Releases the caller's hold on a snapshot; the manager and the scopes holding it keep it alive.
///
/// # Safety
/// `snapshot` must be null or a snapshot from [`fdb_scope_commit`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_snapshot_release(snapshot: *mut FdbSnapshot) -> i32 {
    ffi_guard(Export::SnapshotRelease, || {
        if !snapshot.is_null() {
            // SAFETY: the caller hands back ownership of a snapshot.
            drop(unsafe { Box::from_raw(snapshot) });
        }
        Ok(())
    })
}

/// `AddSnapshot` up to its compaction for a committed snapshot, which the manager then shares with the caller:
/// registers it for [`crate::manager_exports::fdb_queue_compaction`] to compact. [`FdbStatus::Rejected`] when its block
/// is at or below the persisted block, [`FdbStatus::Duplicate`] when its state is already added.
///
/// # Safety
/// `mgr` must be a live manager and `snapshot` a live snapshot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_add_snapshot(mgr: *const FdbManager, snapshot: *const FdbSnapshot) -> i32 {
    ffi_guard(Export::AddSnapshot, || {
        // SAFETY: the caller passes a live manager and a live snapshot.
        let (mgr, snapshot) = unsafe { (mgr_ref(mgr)?, snapshot_ref(snapshot)?) };
        let to = snapshot.to();
        match mgr
            .poison
            .guard(|| Ok(mgr.manager.register_snapshot(Arc::clone(snapshot))?))?
        {
            AddSnapshotOutcome::Added { .. } => Ok(()),
            AddSnapshotOutcome::Rejected => Err(refused(FdbStatus::Rejected, &to)),
            AddSnapshotOutcome::Duplicate => Err(refused(FdbStatus::Duplicate, &to)),
        }
    })
}

/// A snapshot from `from` to `to` holding the `len` bytes of records of `records` (see [`crate::snapshot_stream`]),
/// for tests to add without a scope.
///
/// # Safety
/// `from` and `to` must be valid, `records` valid for `len` bytes and `snapshot` valid for writes.
#[cfg(any(test, feature = "test-api"))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_test_snapshot_from_records(
    from: *const FfiStateId,
    to: *const FfiStateId,
    records: *const u8,
    len: usize,
    snapshot: *mut *mut FdbSnapshot,
) -> i32 {
    ffi_guard(Export::Other, || {
        // SAFETY: the caller passes valid pointers.
        let (from, to, records, snapshot) = unsafe { (state(from)?, state(to)?, bytes(records, len)?, out(snapshot)?) };
        let mut builder = SnapshotContentBuilder::new();
        crate::snapshot_stream::append(&mut builder, records)?;
        *snapshot = Box::into_raw(Box::new(Arc::new(Snapshot::new(from, to, builder.build()))));
        Ok(())
    })
}

/// Copies every entry of `snapshot` as records (see [`crate::snapshot_stream`]) into `buffer`, its accounts, clears,
/// slots, state trie nodes then storage trie nodes, each kind sorted as the C# comparers sort it, and stores their
/// length in `len`; a dump longer than `capacity` yields [`FdbStatus::BufferTooSmall`] with its length in `len`.
///
/// # Safety
/// `snapshot` must be a live snapshot, `buffer` valid for writes of `capacity` bytes and `len` valid for writes.
#[cfg(any(test, feature = "test-api"))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_test_snapshot_dump(
    snapshot: *const FdbSnapshot,
    buffer: *mut u8,
    capacity: u64,
    len: *mut u64,
) -> i32 {
    ffi_guard(Export::Other, || {
        // SAFETY: the caller passes a live snapshot and valid pointers.
        let (snapshot, buffer, len) = unsafe {
            (
                snapshot_ref(snapshot)?,
                bytes_mut(buffer, capacity as usize)?,
                out(len)?,
            )
        };
        let dump = crate::snapshot_stream::dump(snapshot);
        *len = dump.len() as u64;
        if dump.len() > buffer.len() {
            return Err(FfiError {
                status: FdbStatus::BufferTooSmall,
                message: format!("the dump has {} bytes, the buffer {capacity}", dump.len()),
            });
        }
        buffer[..dump.len()].copy_from_slice(&dump);
        Ok(())
    })
}

/// The bundle of `base` for a read-only bundle or a scope.
fn gather(mgr: &FdbManager, base: StateId, reader_flags: u32) -> Result<FdbRob, FfiError> {
    let bundle = mgr.poison.guard(|| {
        Ok(mgr
            .manager
            .gather_read_only_snapshot_bundle(&base, ReaderFlags::from_bits(reader_flags))?)
    })?;
    Ok(FdbRob::new(bundle, &mgr.poison, mgr.manager.stats()))
}

fn write_account(read: (Option<Account>, ReadSource), account: &mut FfiAccount, found: &mut u8, source: &mut u8) {
    let (read, from) = read;
    *found = read.is_some().into();
    if let Some(read) = read {
        *account = FfiAccount::from(&read);
    }
    *source = from as u8;
}

/// A reader copying an RLP into `buffer`, as a key-value read copies a value.
fn copy_into(buffer: &mut [u8]) -> impl FnOnce(&[u8]) -> GetInto + '_ {
    |rlp| match buffer.get_mut(..rlp.len()) {
        Some(target) => {
            target.copy_from_slice(rlp);
            GetInto::Found(rlp.len())
        }
        None => GetInto::BufferTooSmall(rlp.len()),
    }
}

fn to_node_records(records: &[FfiNodeRecord], blob_len: usize) -> Result<Vec<NodeRecord>, FfiError> {
    records.iter().map(|record| record.to_node_record(blob_len)).collect()
}

/// # Safety
/// `records` must be valid for reads of `count` records unless `count` is 0.
unsafe fn node_records<'a>(records: *const FfiNodeRecord, count: u32) -> Result<&'a [FfiNodeRecord], FfiError> {
    match (records.is_null(), count) {
        (_, 0) => Ok(&[]),
        (true, _) => Err(FfiError::invalid_argument("null records with a non-zero count")),
        // SAFETY: the caller guarantees `count` readable records; C# passes them aligned.
        (false, _) => Ok(unsafe { slice::from_raw_parts(records, count as usize) }),
    }
}

/// # Safety
/// `rob` must be null or a live bundle or scope.
unsafe fn rob_ref<'a>(rob: *const FdbRob) -> Result<&'a FdbRob, FfiError> {
    // SAFETY: the caller passes null or a live bundle; a scope begins with its bundle.
    unsafe { rob.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null bundle"))
}

/// # Safety
/// `scope` must be null or a live scope.
unsafe fn scope_ref<'a>(scope: *const FdbScope) -> Result<&'a FdbScope, FfiError> {
    // SAFETY: the caller passes null or a live scope.
    unsafe { scope.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null scope"))
}

/// # Safety
/// `snapshot` must be null or a live snapshot.
unsafe fn snapshot_ref<'a>(snapshot: *const FdbSnapshot) -> Result<&'a Arc<Snapshot>, FfiError> {
    // SAFETY: the caller passes null or a live snapshot.
    unsafe { snapshot.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null snapshot"))
}

#[cfg(test)]
mod tests {
    use std::ptr;

    use flatdb_core::seam::{self, BundleRead};
    use flatdb_core::types::{Hash256, TreePath, U256};
    use rstest::rstest;

    use super::*;
    use crate::faults::Fault;
    use crate::fdb_store_destroy;
    use crate::manager_exports::tests::{
        A, B, CFG, OWNER, TestHost, block, create, fdb_host, has_state, inject, last_error, mem_store, parent, records,
    };
    use crate::manager_exports::{fdb_destroy, fdb_queue_compaction};
    use crate::op_stream::writer::OpWriter;
    use crate::snapshot_stream;
    use crate::store::FdbStore;

    const OK: i32 = FdbStatus::Ok as i32;
    const CURRENT: u8 = ReadSource::Current as u8;
    const OWN: u8 = ReadSource::Own as u8;
    const SNAPSHOT: u8 = ReadSource::Snapshot as u8;
    const PERSISTENCE: u8 = ReadSource::Persistence as u8;
    const SELF_DESTRUCTED: u8 = ReadSource::SelfDestructed as u8;
    const MAIN: FfiScopeFlags = FfiScopeFlags {
        filter_in_memory_slot_reads: 0,
        usage: 0,
        pad: [0; 2],
    };
    const B_HASH: Hash256 = [0x0B; 32];
    const INLINE: Hash256 = [0; 32];

    /// A manager over a fresh in-memory store, with the blocks of `records` from 0 to a head added.
    struct Node {
        _host: Box<TestHost>,
        store: *mut FdbStore,
        mgr: *mut FdbManager,
    }

    impl Node {
        fn new(head: u64) -> Self {
            let host = Box::new(TestHost::default());
            let store = mem_store();
            let (status, mgr) = create(store, CFG, &fdb_host(&host));
            assert_eq!(status, OK, "{}", last_error());
            let node = Self {
                _host: host,
                store,
                mgr,
            };
            for number in 0..=head {
                let snapshot = snapshot_of(parent(number), block(number), &records(number));
                assert_eq!(node.add(snapshot), OK, "{}", last_error());
                release(snapshot);
            }
            node
        }

        /// Adds the snapshot, then compacts it, returning the first status that is not Ok.
        fn add(&self, snapshot: *mut FdbSnapshot) -> i32 {
            let mut stall = 0i64;
            // SAFETY: the manager and the snapshot are live.
            unsafe {
                let added = fdb_add_snapshot(self.mgr, snapshot);
                if added != OK {
                    return added;
                }
                fdb_queue_compaction(self.mgr, &(*snapshot).to().into(), &mut stall)
            }
        }

        /// Destroys the manager, then the store, which every handle created over it must have released.
        fn close(self) {
            // SAFETY: the manager and the store are destroyed once.
            unsafe {
                assert_eq!(fdb_destroy(self.mgr), OK);
                assert_eq!(
                    fdb_store_destroy(self.store),
                    OK,
                    "a handle over the store is still open"
                );
            }
        }
    }

    fn snapshot_of(from: StateId, to: StateId, records: &[u8]) -> *mut FdbSnapshot {
        let mut snapshot = ptr::null_mut();
        // SAFETY: every pointer is valid.
        let status = unsafe {
            fdb_test_snapshot_from_records(&from.into(), &to.into(), records.as_ptr(), records.len(), &mut snapshot)
        };
        assert_eq!(status, OK, "{}", last_error());
        snapshot
    }

    fn release(snapshot: *mut FdbSnapshot) {
        // SAFETY: the snapshot is released once.
        assert_eq!(unsafe { fdb_snapshot_release(snapshot) }, OK);
    }

    fn dump(snapshot: *const FdbSnapshot) -> Vec<u8> {
        let mut len = 0u64;
        // SAFETY: the snapshot is live; a null buffer of no capacity asks for the length.
        let status = unsafe { fdb_test_snapshot_dump(snapshot, ptr::null_mut(), 0, &mut len) };
        assert_eq!(status, FdbStatus::BufferTooSmall as i32);
        let mut buffer = vec![0u8; len as usize];
        // SAFETY: the buffer holds `len` bytes.
        let status = unsafe { fdb_test_snapshot_dump(snapshot, buffer.as_mut_ptr(), len, &mut len) };
        assert_eq!((status, len as usize), (OK, buffer.len()));
        buffer
    }

    fn gather(mgr: *mut FdbManager, base: StateId) -> (*mut FdbRob, FfiRobInfo) {
        let (mut rob, mut info) = (ptr::null_mut(), FfiRobInfo::default());
        // SAFETY: the manager is live and every pointer valid.
        let status = unsafe { fdb_rob_gather(mgr, &base.into(), 0, &mut rob, &mut info) };
        assert_eq!(status, OK, "{}", last_error());
        (rob, info)
    }

    fn open(mgr: *mut FdbManager, base: StateId, flags: &FfiScopeFlags) -> (*mut FdbScope, FfiRobInfo) {
        let (mut scope, mut info) = (ptr::null_mut(), FfiRobInfo::default());
        // SAFETY: the manager is live and every pointer valid.
        let status = unsafe { fdb_scope_open(mgr, &base.into(), 0, flags, &mut scope, &mut info) };
        assert_eq!(status, OK, "{}", last_error());
        (scope, info)
    }

    fn account(nonce: u64) -> FfiAccount {
        FfiAccount::from(&Account::new(nonce, U256::from_u64(nonce)))
    }

    /// The nonce of the account the scope reads, and where it read it.
    fn scope_account(scope: *const FdbScope, address: &[u8; 20], promote: u8) -> (Option<u64>, u8) {
        let (mut read, mut found, mut source) = (FfiAccount::default(), 0u8, 0u8);
        // SAFETY: the scope is live and every pointer valid.
        let status =
            unsafe { fdb_scope_get_account(scope, address.as_ptr(), promote, &mut read, &mut found, &mut source) };
        assert_eq!(status, OK, "{}", last_error());
        ((found != 0).then_some(read.nonce), source)
    }

    fn rob_account(rob: *const FdbRob, address: &[u8; 20]) -> (Option<u64>, u8) {
        let (mut read, mut found, mut source) = (FfiAccount::default(), 0u8, 0u8);
        // SAFETY: the bundle is live and every pointer valid.
        let status = unsafe { fdb_rob_get_account(rob, address.as_ptr(), &mut read, &mut found, &mut source) };
        assert_eq!(status, OK, "{}", last_error());
        ((found != 0).then_some(read.nonce), source)
    }

    fn scope_self_destruct_idx(scope: *const FdbScope, address: &[u8; 20]) -> i32 {
        let mut idx = 0;
        // SAFETY: the scope is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_scope_self_destruct_idx(scope, address.as_ptr(), &mut idx) },
            OK
        );
        idx
    }

    fn rob_self_destruct_idx(rob: *const FdbRob, address: &[u8; 20]) -> i32 {
        let mut idx = 0;
        // SAFETY: the bundle is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_rob_self_destruct_idx(rob, address.as_ptr(), &mut idx) },
            OK
        );
        idx
    }

    fn scope_slot(scope: *const FdbScope, address: &[u8; 20], slot: u64, sd_idx: i32) -> (Option<U256>, u8) {
        let (mut value, mut found, mut source) = ([0u64; 4], 0u8, 0u8);
        let slot = U256::from_u64(slot).0;
        // SAFETY: the scope is live and every pointer valid.
        let status = unsafe {
            fdb_scope_get_slot(
                scope,
                address.as_ptr(),
                slot.as_ptr(),
                sd_idx,
                value.as_mut_ptr(),
                &mut found,
                &mut source,
            )
        };
        assert_eq!(status, OK, "{}", last_error());
        ((found != 0).then_some(U256(value)), source)
    }

    fn rob_slot(rob: *const FdbRob, address: &[u8; 20], slot: u64, sd_idx: i32, filtered: u8) -> (Option<U256>, u8) {
        let (mut value, mut found, mut source) = ([0u64; 4], 0u8, 0u8);
        let slot = U256::from_u64(slot).0;
        // SAFETY: the bundle is live and every pointer valid.
        let status = unsafe {
            fdb_rob_get_slot(
                rob,
                address.as_ptr(),
                slot.as_ptr(),
                sd_idx,
                filtered,
                value.as_mut_ptr(),
                &mut found,
                &mut source,
            )
        };
        assert_eq!(status, OK, "{}", last_error());
        ((found != 0).then_some(U256(value)), source)
    }

    fn set_slot(scope: *const FdbScope, address: &[u8; 20], slot: u64, value: u64) {
        let (slot, value) = (U256::from_u64(slot).0, U256::from_u64(value).0);
        // SAFETY: the scope is live and every pointer valid.
        let status = unsafe { fdb_scope_set_slot(scope, address.as_ptr(), slot.as_ptr(), value.as_ptr()) };
        assert_eq!(status, OK, "{}", last_error());
    }

    /// The status of a load of the state trie node at `path` through the scope, the RLP and where it was read.
    fn scope_state_rlp(scope: *const FdbScope, path: &str, hash: &Hash256, capacity: usize) -> (i32, Vec<u8>, i64, u8) {
        let path = TreePath::from_hex(path);
        let (mut buffer, mut len, mut source) = (vec![0u8; capacity], 0i64, 0u8);
        // SAFETY: the scope is live and every pointer valid.
        let status = unsafe {
            fdb_scope_load_state_rlp(
                scope,
                path.path.as_ptr(),
                path.length,
                hash.as_ptr(),
                0,
                buffer.as_mut_ptr(),
                capacity,
                &mut len,
                &mut source,
            )
        };
        buffer.truncate(if status == OK { len.max(0) as usize } else { 0 });
        (status, buffer, len, source)
    }

    fn scope_storage_rlp(scope: *const FdbScope, path: &str, hash: &Hash256) -> (i32, Vec<u8>, u8) {
        let path = TreePath::from_hex(path);
        let (mut buffer, mut len, mut source) = ([0u8; 64], 0i64, 0u8);
        // SAFETY: the scope is live and every pointer valid.
        let status = unsafe {
            fdb_scope_load_storage_rlp(
                scope,
                OWNER.as_ptr(),
                path.path.as_ptr(),
                path.length,
                hash.as_ptr(),
                0,
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut len,
                &mut source,
            )
        };
        (status, buffer[..len.max(0) as usize].to_vec(), source)
    }

    fn rob_state_rlp(rob: *const FdbRob, path: &str) -> (Vec<u8>, i64, u8) {
        let path = TreePath::from_hex(path);
        let (mut buffer, mut len, mut source) = ([0u8; 64], 0i64, 0u8);
        // SAFETY: the bundle is live and every pointer valid; the hash is not compared.
        let status = unsafe {
            fdb_rob_load_state_rlp(
                rob,
                path.path.as_ptr(),
                path.length,
                [0xFF; 32].as_ptr(),
                0,
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut len,
                &mut source,
            )
        };
        assert_eq!(status, OK, "{}", last_error());
        (buffer[..len.max(0) as usize].to_vec(), len, source)
    }

    fn rob_storage_rlp(rob: *const FdbRob, path: &str) -> (Vec<u8>, u8) {
        let path = TreePath::from_hex(path);
        let (mut buffer, mut len, mut source) = ([0u8; 64], 0i64, 0u8);
        // SAFETY: the bundle is live and every pointer valid; the hash is not compared.
        let status = unsafe {
            fdb_rob_load_storage_rlp(
                rob,
                OWNER.as_ptr(),
                path.path.as_ptr(),
                path.length,
                [0xFF; 32].as_ptr(),
                0,
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut len,
                &mut source,
            )
        };
        assert_eq!(status, OK, "{}", last_error());
        (buffer[..len.max(0) as usize].to_vec(), source)
    }

    /// The records of the nodes `(path, hash, rlp)`, with their RLP laid out in one blob.
    fn node_batch(nodes: &[(&str, Hash256, &[u8])]) -> (Vec<FfiNodeRecord>, Vec<u8>) {
        let mut blob = Vec::new();
        let records = nodes
            .iter()
            .map(|(path, hash, rlp)| {
                let path = TreePath::from_hex(path);
                let record = FfiNodeRecord {
                    path: path.path,
                    path_len: path.length,
                    rlp_len: rlp.len() as u32,
                    rlp_offset: blob.len() as u64,
                    hash: *hash,
                    ..FfiNodeRecord::default()
                };
                blob.extend_from_slice(rlp);
                record
            })
            .collect();
        (records, blob)
    }

    fn commit(scope: *const FdbScope, from: StateId, to: StateId, keep: bool) -> *mut FdbSnapshot {
        let mut snapshot = ptr::null_mut();
        // SAFETY: the scope is live and every pointer valid.
        let status = unsafe { fdb_scope_commit(scope, &from.into(), &to.into(), keep.into(), &mut snapshot) };
        assert_eq!(status, OK, "{}", last_error());
        snapshot
    }

    /// A scope reads its current content, then its own snapshots, then the bundle it was opened over, and its base view
    /// the bundle alone; its loads are hash-checked, the bundle's are not. A kept commit hands out a snapshot holding
    /// exactly the scope's writes, which the manager adds once and a scope can be seeded with.
    #[test]
    fn a_scope_reads_through_its_layers_and_commits_what_the_manager_adds() {
        let node = Node::new(5);
        let (rob, rob_info) = gather(node.mgr, block(5));
        let (scope, info) = open(node.mgr, block(5), &MAIN);
        let base = scope.cast::<FdbRob>().cast_const();
        let chain = rob_info.snapshot_count as i32;
        let before = seam::this_thread();
        let (r1, r2, r3) = ([0xC3, 0x01, 0x02], [0xC4, 0x02, 0x02, 0x02], [0xC2, 0x03, 0x03]);
        let (h1, h2, h4, hs, hs5) = ([0x11; 32], [0x22; 32], [0x44; 32], [0x55; 32], [0x56; 32]);
        let (rs, rs5) = ([0xC1, 0x55], [0xC1, 0x56]);

        // SAFETY: the scope is live, every pointer valid and the batches as long as their counts.
        unsafe {
            assert_eq!(fdb_scope_set_account(scope, B.as_ptr(), &account(7)), OK);
            assert_eq!(fdb_scope_promote_account(scope, B.as_ptr(), &account(1)), OK);
            assert_eq!(fdb_scope_clear_storage(scope, B.as_ptr(), B_HASH.as_ptr()), OK);
            assert_eq!(
                fdb_scope_set_state_node(
                    scope,
                    TreePath::from_hex("").path.as_ptr(),
                    0,
                    h1.as_ptr(),
                    r1.as_ptr(),
                    3
                ),
                OK
            );
            let (records, blob) = node_batch(&[("2", h2, &r2), ("3", INLINE, &r3), ("4", h4, &[])]);
            assert_eq!(
                fdb_scope_put_state_nodes(scope, records.as_ptr(), 3, blob.as_ptr(), blob.len()),
                OK
            );
            assert_eq!(
                fdb_scope_set_storage_node(
                    scope,
                    OWNER.as_ptr(),
                    TreePath::from_hex("").path.as_ptr(),
                    0,
                    hs.as_ptr(),
                    rs.as_ptr(),
                    2
                ),
                OK
            );
            let (records, blob) = node_batch(&[("5", hs5, &rs5)]);
            assert_eq!(
                fdb_scope_put_storage_nodes(scope, OWNER.as_ptr(), records.as_ptr(), 1, blob.as_ptr(), blob.len()),
                OK
            );
        }
        set_slot(scope, &A, 1, 0);
        set_slot(scope, &A, 9, 9);

        assert_eq!(
            (
                info,
                rob_info.reader_state,
                rob_info.has_self_destructs,
                rob_info.own_count
            ),
            (rob_info, StateId::PRE_GENESIS.into(), 1, 0),
            "the scope opens over the shared bundle"
        );
        assert_eq!(scope_account(scope, &A, 0), (Some(1), SNAPSHOT));
        assert_eq!(
            scope_account(scope, &B, 0),
            (Some(7), CURRENT),
            "promote never overwrites a write"
        );
        assert_eq!(
            rob_account(base, &B),
            (None, PERSISTENCE),
            "the base view reads the bundle alone"
        );
        assert_eq!(
            (
                scope_self_destruct_idx(scope, &B),
                rob_self_destruct_idx(base, &B),
                rob_self_destruct_idx(base, &A)
            ),
            (chain, chain - 1, -1),
            "the current content's clear is numbered after the bundle's snapshots"
        );
        assert_eq!(scope_slot(scope, &B, 2, chain), (None, SELF_DESTRUCTED));
        assert_eq!(
            scope_slot(scope, &B, 2, chain - 1),
            (Some(U256::from_u64(5)), SNAPSHOT),
            "a stale index"
        );
        assert_eq!(scope_slot(scope, &A, 1, -1), (None, CURRENT), "a zero write deletes");
        assert_eq!(scope_slot(scope, &A, 9, -1), (Some(U256::from_u64(9)), CURRENT));

        assert_eq!(scope_state_rlp(scope, "", &h1, 64), (OK, r1.to_vec(), 3, CURRENT));
        assert_eq!(scope_state_rlp(scope, "3", &INLINE, 64), (OK, r3.to_vec(), 3, CURRENT));
        assert_eq!(
            scope_state_rlp(scope, "4", &h4, 64),
            (OK, vec![], -1, PERSISTENCE),
            "a placeholder reads the empty database"
        );
        assert_eq!(
            scope_state_rlp(scope, "1", &[0xEE; 32], 64),
            (OK, vec![], -1, PERSISTENCE)
        );
        assert_eq!(
            scope_state_rlp(scope, "2", &h2, 1),
            (FdbStatus::BufferTooSmall as i32, vec![], 4, CURRENT),
            "the length to retry with"
        );
        assert_eq!(scope_state_rlp(scope, "2", &h2, 4), (OK, r2.to_vec(), 4, CURRENT));
        let mismatch = scope_state_rlp(scope, "", &[5; 32], 64).0;
        assert_eq!(
            (mismatch, last_error().starts_with("Node hash mismatch")),
            (FdbStatus::HashMismatch as i32, true)
        );
        assert_eq!(
            rob_state_rlp(base, ""),
            (vec![0xC1, 5], 2, SNAPSHOT),
            "the bundle's load is not hash-checked"
        );
        assert_eq!(scope_storage_rlp(scope, "5", &hs5), (OK, rs5.to_vec(), CURRENT));
        assert_eq!(
            scope_storage_rlp(scope, "", &[0x85; 32]).0,
            FdbStatus::HashMismatch as i32
        );

        let kept = commit(scope, block(5), block(6), true);
        assert_eq!(scope_account(scope, &B, 0), (Some(7), OWN));
        assert_eq!(scope_state_rlp(scope, "", &h1, 64), (OK, r1.to_vec(), 3, OWN));
        assert!(
            commit(scope, block(6), block(7), false).is_null(),
            "a commit not kept hands nothing out"
        );
        assert_eq!(
            scope_self_destruct_idx(scope, &B),
            chain,
            "the commit not kept still numbers its snapshot"
        );
        assert_eq!(
            (node.add(kept), node.add(kept), has_state(node.mgr, block(6))),
            (OK, FdbStatus::Duplicate as i32, true)
        );

        // The same writes as records, in another order, make the same snapshot.
        let mut expected = OpWriter::default();
        expected
            .op(snapshot_stream::STORAGE_NODE)
            .hash(&OWNER)
            .path(&TreePath::from_hex("5"))
            .hash(&hs5)
            .bytes(&rs5)
            .op(snapshot_stream::SET_SLOT)
            .address(&A)
            .u256(&U256::from_u64(9))
            .u256(&U256::from_u64(9))
            .op(snapshot_stream::DELETE_SLOT)
            .address(&A)
            .u256(&U256::from_u64(1))
            .op(snapshot_stream::SET_ACCOUNT)
            .address(&B)
            .account(&Account::new(7, U256::from_u64(7)))
            .op(snapshot_stream::SELF_DESTRUCT)
            .address(&B)
            .byte(1)
            .op(snapshot_stream::STORAGE_NODE)
            .hash(&OWNER)
            .path(&TreePath::from_hex(""))
            .hash(&hs)
            .bytes(&rs)
            .op(snapshot_stream::STATE_NODE)
            .path(&TreePath::from_hex("4"))
            .hash(&h4)
            .bytes(&[])
            .op(snapshot_stream::STATE_NODE)
            .path(&TreePath::from_hex("3"))
            .hash(&INLINE)
            .bytes(&r3)
            .op(snapshot_stream::STATE_NODE)
            .path(&TreePath::from_hex("2"))
            .hash(&h2)
            .bytes(&r2)
            .op(snapshot_stream::STATE_NODE)
            .path(&TreePath::from_hex(""))
            .hash(&h1)
            .bytes(&r1);
        let expected = snapshot_of(block(5), block(6), &expected.0);
        assert_eq!(dump(kept), dump(expected));

        // Below block 5 no snapshot clears a storage, so only the seed tells the scope it has a clear.
        let (no_clears, no_clears_info) = gather(node.mgr, block(4));
        let (mut seeded, mut seeded_info) = (ptr::null_mut(), FfiRobInfo::default());
        let own = [kept.cast_const()];
        // SAFETY: the bundle and the snapshot are live and every pointer valid.
        let status = unsafe { fdb_scope_create(no_clears, own.as_ptr(), 1, &MAIN, &mut seeded, &mut seeded_info) };
        assert_eq!(status, OK, "{}", last_error());
        assert_eq!(
            (
                no_clears_info.has_self_destructs,
                seeded_info.own_count,
                seeded_info.has_self_destructs
            ),
            (0, 1, 1)
        );
        assert_eq!(scope_account(seeded, &B, 1), (Some(7), OWN));
        assert_eq!(scope_account(seeded, &B, 0), (Some(7), CURRENT), "promoted");
        assert_eq!(
            scope_self_destruct_idx(seeded, &B),
            no_clears_info.snapshot_count as i32,
            "the seed is numbered after the bundle"
        );
        let (added, _) = gather(node.mgr, block(6));
        assert_eq!(rob_account(added, &B), (Some(7), SNAPSHOT));

        let after = seam::this_thread();
        let counted = |read: BundleRead| after[read as usize] - before[read as usize];
        assert_eq!(
            [
                BundleRead::AccountCurrent,
                BundleRead::AccountOwn,
                BundleRead::StorageCurrent,
                BundleRead::StateNodeCurrent,
                BundleRead::StateNodeOwn,
                BundleRead::StorageNodeCurrent,
            ]
            .map(counted),
            [2, 2, 2, 4, 1, 1]
        );

        // SAFETY: every handle is released once.
        unsafe {
            for snapshot in [kept, expected] {
                release(snapshot);
            }
            for scope in [scope, seeded] {
                assert_eq!(fdb_scope_release(scope), OK);
            }
            for rob in [rob, added, no_clears] {
                assert_eq!(fdb_rob_release(rob), OK);
            }
        }
        node.close();
    }

    /// Slot reads below a scope and of its base view apply the caller's self-destruct index, stale or fresh, and only a
    /// filtered scope or read builds the bundle's slot filter, which answers as the plain loop does.
    #[rstest]
    fn slot_reads_below_a_scope_apply_the_callers_self_destruct_index(
        #[values(0, 1)] scope_filtered: u8,
        #[values(0, 1)] filtered: u8,
    ) {
        let node = Node::new(5);
        let flags = FfiScopeFlags {
            filter_in_memory_slot_reads: scope_filtered,
            ..MAIN
        };
        let (scope, info) = open(node.mgr, block(5), &flags);
        let base = scope.cast::<FdbRob>().cast_const();
        // SAFETY: the scope is live.
        let filter_built = || unsafe { &*base }.bundle.slot_filter().is_some();
        let cleared = info.snapshot_count as i32 - 1;
        let written = (Some(U256::from_u64(5)), SNAPSHOT);
        let expected = [written, (None, SELF_DESTRUCTED), (None, PERSISTENCE)];

        let scope_reads = [
            scope_slot(scope, &B, 2, cleared),
            scope_slot(scope, &B, 3, cleared),
            scope_slot(scope, &B, 3, -1),
        ];
        let built_by_scope = filter_built();
        let base_reads = [
            rob_slot(base, &B, 2, cleared, filtered),
            rob_slot(base, &B, 3, cleared, filtered),
            rob_slot(base, &B, 3, -1, filtered),
        ];

        assert_eq!(
            (scope_reads, base_reads),
            (expected, expected),
            "a fresh index cuts the read off at the clear, a stale one reads through"
        );
        assert_eq!(rob_slot(base, &A, 1, -1, filtered), written);
        assert_eq!(
            (info.may_filter_slots, built_by_scope, filter_built()),
            (1, scope_filtered != 0, scope_filtered | filtered != 0)
        );
        assert_eq!(rob_storage_rlp(base, ""), (vec![0xC2, 0x80, 0x05], SNAPSHOT));

        // SAFETY: the scope is released once.
        assert_eq!(unsafe { fdb_scope_release(scope) }, OK);
        node.close();
    }

    /// A misuse the library refuses.
    #[derive(Clone, Copy, Debug)]
    enum Misuse {
        UnknownState,
        UnknownStateScope,
        UnknownUsage,
        LongPath,
        RecordOutsideBlob,
        NullSeed,
    }

    /// Bad arguments fail the call with a status and a message, and leave nothing half-written.
    #[rstest]
    #[case(Misuse::UnknownState, FdbStatus::StateNotRetained, "No state available")]
    #[case(Misuse::UnknownStateScope, FdbStatus::StateNotRetained, "No state available")]
    #[case(Misuse::UnknownUsage, FdbStatus::InvalidArgument, "unknown content usage 3")]
    #[case(Misuse::LongPath, FdbStatus::InvalidArgument, "path length 65")]
    #[case(
        Misuse::RecordOutsideBlob,
        FdbStatus::InvalidArgument,
        "node RLP at 1 of 2 bytes outside a blob of 2 bytes"
    )]
    #[case(Misuse::NullSeed, FdbStatus::InvalidArgument, "null own snapshots")]
    fn misuses_are_refused(#[case] misuse: Misuse, #[case] status: FdbStatus, #[case] message: &str) {
        let node = Node::new(1);
        let (scope, _) = open(node.mgr, block(1), &MAIN);
        let (mut rob, mut created, mut info) = (ptr::null_mut(), ptr::null_mut(), FfiRobInfo::default());
        let unknown: FfiStateId = StateId::new(1, [0; 32]).into();
        let path = TreePath::from_hex("2");
        // SAFETY: every handle is live and every pointer valid, as long as the counts it is passed with.
        let refused = unsafe {
            match misuse {
                Misuse::UnknownState => fdb_rob_gather(node.mgr, &unknown, 0, &mut rob, &mut info),
                Misuse::UnknownStateScope => fdb_scope_open(node.mgr, &unknown, 0, &MAIN, &mut created, &mut info),
                Misuse::UnknownUsage => {
                    let flags = FfiScopeFlags { usage: 3, ..MAIN };
                    fdb_scope_open(node.mgr, &block(1).into(), 0, &flags, &mut created, &mut info)
                }
                Misuse::LongPath => {
                    fdb_scope_set_state_node(scope, path.path.as_ptr(), 65, INLINE.as_ptr(), ptr::null(), 0)
                }
                Misuse::RecordOutsideBlob => {
                    let (mut records, blob) = node_batch(&[("2", [0x22; 32], &[0xC1, 0x01])]);
                    records.push(FfiNodeRecord {
                        rlp_offset: 1,
                        ..records[0]
                    });
                    fdb_scope_put_state_nodes(scope, records.as_ptr(), 2, blob.as_ptr(), blob.len())
                }
                Misuse::NullSeed => fdb_scope_create(scope.cast(), ptr::null(), 1, &MAIN, &mut created, &mut info),
            }
        };

        assert_eq!(refused, status as i32);
        assert!(last_error().contains(message), "{}", last_error());
        assert!(rob.is_null() && created.is_null());
        assert_eq!(
            scope_state_rlp(scope, "2", &[0x22; 32], 64),
            (OK, vec![], -1, PERSISTENCE),
            "nothing written"
        );
        // SAFETY: the scope is released once.
        assert_eq!(unsafe { fdb_scope_release(scope) }, OK);
        node.close();
    }

    /// A call that panics.
    #[derive(Clone, Copy, Debug)]
    enum Call {
        SetAccount,
        PromoteAccount,
        SetSlot,
        ClearStorage,
        SetStateNode,
        SetStorageNode,
        PutStateNodes,
        PutStorageNodes,
        Commit,
        LoadThroughScope,
        LoadThroughBundle,
        AddSnapshot,
    }

    /// A panic in any write, commit or load poisons the store: every later call on its scopes, bundles and manager
    /// fails with Poisoned, while the releases still work.
    #[rstest]
    fn a_panic_in_any_call_poisons_the_store(
        #[values(
            Call::SetAccount,
            Call::PromoteAccount,
            Call::SetSlot,
            Call::ClearStorage,
            Call::SetStateNode,
            Call::SetStorageNode,
            Call::PutStateNodes,
            Call::PutStorageNodes,
            Call::Commit,
            Call::LoadThroughScope,
            Call::LoadThroughBundle,
            Call::AddSnapshot
        )]
        call: Call,
    ) {
        let node = Node::new(1);
        let (scope, _) = open(node.mgr, block(1), &MAIN);
        let (rob, _) = gather(node.mgr, block(1));
        let snapshot = snapshot_of(block(1), block(2), &records(2));
        let (records, blob) = node_batch(&[("2", [0x22; 32], &[0xC1, 0x01])]);
        let (path, slot) = (TreePath::from_hex("2"), U256::from_u64(1).0);
        let (mut buffer, mut len, mut source, mut kept) = ([0u8; 8], 0i64, 0u8, ptr::null_mut());
        inject(node.store, Fault::PanicInExport, "injected");

        // SAFETY: every handle is live and every pointer valid, as long as the counts it is passed with.
        let panicked = unsafe {
            match call {
                Call::SetAccount => fdb_scope_set_account(scope, A.as_ptr(), &account(1)),
                Call::PromoteAccount => fdb_scope_promote_account(scope, A.as_ptr(), ptr::null()),
                Call::SetSlot => fdb_scope_set_slot(scope, A.as_ptr(), slot.as_ptr(), slot.as_ptr()),
                Call::ClearStorage => fdb_scope_clear_storage(scope, A.as_ptr(), B_HASH.as_ptr()),
                Call::SetStateNode => {
                    fdb_scope_set_state_node(scope, path.path.as_ptr(), 1, INLINE.as_ptr(), blob.as_ptr(), 2)
                }
                Call::SetStorageNode => fdb_scope_set_storage_node(
                    scope,
                    OWNER.as_ptr(),
                    path.path.as_ptr(),
                    1,
                    INLINE.as_ptr(),
                    blob.as_ptr(),
                    2,
                ),
                Call::PutStateNodes => fdb_scope_put_state_nodes(scope, records.as_ptr(), 1, blob.as_ptr(), 2),
                Call::PutStorageNodes => {
                    fdb_scope_put_storage_nodes(scope, OWNER.as_ptr(), records.as_ptr(), 1, blob.as_ptr(), 2)
                }
                Call::Commit => fdb_scope_commit(scope, &block(1).into(), &block(2).into(), 1, &mut kept),
                Call::LoadThroughScope => fdb_scope_load_state_rlp(
                    scope,
                    path.path.as_ptr(),
                    1,
                    INLINE.as_ptr(),
                    0,
                    buffer.as_mut_ptr(),
                    8,
                    &mut len,
                    &mut source,
                ),
                Call::LoadThroughBundle => fdb_rob_load_storage_rlp(
                    rob,
                    OWNER.as_ptr(),
                    path.path.as_ptr(),
                    1,
                    INLINE.as_ptr(),
                    0,
                    buffer.as_mut_ptr(),
                    8,
                    &mut len,
                    &mut source,
                ),
                Call::AddSnapshot => fdb_add_snapshot(node.mgr, snapshot),
            }
        };
        let panic_message = last_error();
        let (mut account, mut found, mut idx) = (FfiAccount::default(), 0u8, 0);
        // SAFETY: as above.
        let later = unsafe {
            [
                fdb_scope_get_account(scope, A.as_ptr(), 0, &mut account, &mut found, &mut source),
                fdb_scope_self_destruct_idx(scope, A.as_ptr(), &mut idx),
                fdb_rob_get_account(scope.cast(), A.as_ptr(), &mut account, &mut found, &mut source),
                fdb_rob_get_account(rob, A.as_ptr(), &mut account, &mut found, &mut source),
                fdb_scope_commit(scope, &block(1).into(), &block(2).into(), 0, &mut kept),
                fdb_add_snapshot(node.mgr, snapshot),
            ]
        };

        assert_eq!(
            (panicked, panic_message.as_str()),
            (FdbStatus::Panic as i32, "panic: injected")
        );
        assert_eq!(later, [FdbStatus::Poisoned as i32; 6]);
        assert!(kept.is_null());
        // SAFETY: every handle is released once.
        unsafe {
            assert_eq!(fdb_scope_release(scope), OK);
            assert_eq!(fdb_rob_release(rob), OK);
        }
        release(snapshot);
        node.close();
    }
}
