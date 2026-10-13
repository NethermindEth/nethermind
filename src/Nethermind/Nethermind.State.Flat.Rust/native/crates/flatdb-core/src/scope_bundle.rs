//! Port of the content side of `Nethermind.State.Flat/SnapshotBundle.cs`: what a scope's bundle holds below the
//! managed caches its C# shell keeps (the transient resource, the trie node cache and the warmer leases).
//!
//! A [`ScopeBundle`] reads through three layers, newest first: the [`MutableContent`] the scope is writing (`current`,
//! created by the first write), the snapshots it committed (`own`, seeded with an overridable scope's chain), and the
//! read-only bundle it was opened over (`chain`). Self-destruct indexes number the chain's snapshots `[0, n)`, the own
//! ones `[n, n + c)` and the current content `n + c`, as C# numbers `_readOnlySnapshotBundle` and `_snapshots`.
//!
//! A reader probes `current` first and loads `own` after it; a commit publishes the frozen snapshot into `own` while it
//! still holds every lock of `current`, so a committed value is always in one of the two. A write racing a commit,
//! which the C# scope rules out by joining its writers first, blocks and lands in the next content.

use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::{Arc, OnceLock};

use arc_swap::ArcSwap;

use crate::bundle::{NodeLoadError, ReadOnlySnapshotBundle, ReadSource, hash_mismatch};
use crate::kv::ReadFlags;
use crate::persistence::{PersistenceReader, PersistenceResult};
use crate::snapshot::Snapshot;
use crate::snapshot::content::TrieNodeRlp;
use crate::snapshot::mutable::{MutableContent, NodeRecord, Usage};
use crate::stats::FlatStats;
use crate::types::{Account, Address, EMPTY_TREE_HASH, Hash256, StateId, TreePath, U256};

/// How a scope reads and sizes its content.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ScopeFlags {
    /// Slot reads that reach the chain go through its negative filter; for read-only execution only.
    pub filter_in_memory_slot_reads: bool,
    pub usage: Usage,
}

pub struct ScopeBundle<R> {
    chain: Arc<ReadOnlySnapshotBundle<R>>,
    /// The committed snapshots, oldest first; replaced only by [`ScopeBundle::commit`].
    own: ArcSwap<Vec<Arc<Snapshot>>>,
    current: OnceLock<Box<MutableContent>>,
    filter_in_memory_slot_reads: bool,
    usage: AtomicU8,
    may_have_self_destructs: bool,
    stats: Arc<FlatStats>,
}

/// What the first in-memory node at a path says about a checked load.
enum Probe<T> {
    Found(T),
    /// A placeholder of the requested hash: the node is read from persistence.
    Placeholder,
}

impl<R: PersistenceReader> ScopeBundle<R> {
    /// A scope over `chain` whose own snapshots start with `own_seed`, oldest first. Slot reads are filtered only when
    /// `flags` asks for it and the chain can build a filter (`SnapshotBundle`'s constructor).
    pub fn new(
        chain: Arc<ReadOnlySnapshotBundle<R>>,
        own_seed: Vec<Arc<Snapshot>>,
        flags: ScopeFlags,
        stats: Arc<FlatStats>,
    ) -> Self {
        let may_have_self_destructs = chain.has_self_destructs()
            || own_seed
                .iter()
                .any(|snapshot| snapshot.counts().self_destructed_storage_addresses > 0);
        Self {
            filter_in_memory_slot_reads: flags.filter_in_memory_slot_reads && chain.may_filter_slots(),
            chain,
            own: ArcSwap::from_pointee(own_seed),
            current: OnceLock::new(),
            usage: AtomicU8::new(flags.usage as u8),
            may_have_self_destructs,
            stats,
        }
    }

    /// The read-only bundle below the scope.
    pub fn chain(&self) -> &Arc<ReadOnlySnapshotBundle<R>> {
        &self.chain
    }

    /// The number of own snapshots, the seed included.
    pub fn own_count(&self) -> usize {
        self.own.load().len()
    }

    /// Whether the chain or the own seed clears a storage; the scope's own clears come on top.
    pub fn may_have_self_destructs(&self) -> bool {
        self.may_have_self_destructs
    }

    pub fn filters_slot_reads(&self) -> bool {
        self.filter_in_memory_slot_reads
    }

    pub fn usage(&self) -> Usage {
        Usage::from_u8(self.usage.load(Ordering::Relaxed))
    }

    /// Port of `GetAccount`: the current content, then the own snapshots, then the chain. With `promote`, an account
    /// read from below the current content is cached in it, as `FlatWorldStateScope.Get` promotes one.
    pub fn get_account(&self, address: &Address, promote: bool) -> PersistenceResult<(Option<Account>, ReadSource)> {
        if let Some(account) = self.current().and_then(|current| current.account(address)) {
            return Ok((account, ReadSource::Current));
        }
        let (account, source) = self.account_below_current(address)?;
        if promote {
            self.current_or_create().promote_account(address, account);
        }
        Ok((account, source))
    }

    /// Port of `PromoteAccount`.
    pub fn promote_account(&self, address: &Address, account: Option<Account>) {
        self.current_or_create().promote_account(address, account);
    }

    /// Port of `SetAccount`; `None` deletes the account.
    pub fn set_account(&self, address: &Address, account: Option<Account>) {
        self.current_or_create().set_account(address, account);
    }

    /// Port of `DetermineSelfDestructSnapshotIdx`: the index of the newest layer that cleared the storage of
    /// `address`, or -1.
    pub fn self_destruct_idx(&self, address: &Address) -> i32 {
        let cleared_in_current = self
            .current()
            .is_some_and(|current| current.self_destruct(address).is_some());
        let chain_count = self.chain.snapshot_count() as i32;
        let own = self.own.load();
        if cleared_in_current {
            return chain_count + own.len() as i32;
        }
        if let Some(index) = own.iter().rposition(|snapshot| snapshot.has_self_destruct(address)) {
            return chain_count + index as i32;
        }
        self.chain.determine_self_destruct_snapshot_idx(address)
    }

    /// Port of `GetSlot` with the caller's self-destruct index: a clear at the tip ends the read below the current
    /// content, one in an own snapshot ends it at that snapshot, and one in the chain is the chain's to apply.
    pub fn get_slot(
        &self,
        address: &Address,
        slot: &U256,
        self_destruct_snapshot_idx: i32,
    ) -> PersistenceResult<(Option<U256>, ReadSource)> {
        if let Some(value) = self.current().and_then(|current| current.slot(address, slot)) {
            return Ok((value, ReadSource::Current));
        }

        let chain_count = self.chain.snapshot_count() as i64;
        let own = self.own.load();
        let cleared = i64::from(self_destruct_snapshot_idx);
        if cleared == chain_count + own.len() as i64 {
            return Ok((None, ReadSource::SelfDestructed));
        }
        let own_cleared = cleared - chain_count;
        for (index, snapshot) in own.iter().enumerate().rev() {
            if let Some(value) = snapshot.try_get_storage(address, slot) {
                return Ok((value, ReadSource::Own));
            }
            if index as i64 <= own_cleared {
                return Ok((None, ReadSource::SelfDestructed));
            }
        }

        if self.filter_in_memory_slot_reads {
            self.chain.get_slot_filtered(address, slot, self_destruct_snapshot_idx)
        } else {
            self.chain.get_slot(address, slot, self_destruct_snapshot_idx)
        }
    }

    /// Port of `SetChangedSlot`: a zero value deletes the slot.
    pub fn set_slot(&self, address: &Address, slot: &U256, value: &U256) {
        self.current_or_create().set_slot(address, slot, value);
    }

    /// Port of `ClearStorage`: records the clear, new when the account below the current content has no storage, and
    /// drops the slots and storage trie nodes the current content holds for it.
    pub fn clear_storage(&self, address: &Address, address_hash: &Hash256) -> PersistenceResult<()> {
        let (account, _) = self.account_below_current(address)?;
        let is_new_account = account.is_none_or(|account| account.storage_root == EMPTY_TREE_HASH);
        let current = self.current_or_create();
        current.set_self_destruct_if_absent(address, is_new_account);
        current.remove_storage(address, address_hash);
        Ok(())
    }

    /// Calls `read` with the RLP of the state trie node at `path`. The first node found at the path, in the current
    /// content, the own snapshots, then the chain's snapshots, must have `hash`: the check of
    /// `StateTrieStoreAdapter.FindCachedOrUnknown`. A placeholder of that hash, or no node, reads persistence.
    pub fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        hash: &Hash256,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> Result<(Option<T>, ReadSource), NodeLoadError> {
        let mut read = Some(read);
        let probed = match self
            .current()
            .and_then(|current| current.with_state_node(path, |node| probe(path, node, hash, &mut read)))
        {
            Some(probed) => Some((probed?, ReadSource::Current)),
            None => self
                .own
                .load()
                .iter()
                .rev()
                .find_map(|snapshot| snapshot.try_get_state_node(path))
                .map(|node| probe(path, node, hash, &mut read))
                .transpose()?
                .map(|probed| (probed, ReadSource::Own)),
        };
        let read = read.take();
        match probed {
            Some((Probe::Found(value), source)) => Ok((Some(value), source)),
            Some((Probe::Placeholder, _)) => Ok(self.chain.load_persisted_state_rlp(path, flags, unread(read))?),
            None => self.chain.load_state_rlp_checked(path, hash, flags, unread(read)),
        }
    }

    /// As [`ScopeBundle::load_state_rlp`], in the storage trie of the account hashing to `address_hash`.
    pub fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        hash: &Hash256,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> Result<(Option<T>, ReadSource), NodeLoadError> {
        let mut read = Some(read);
        let probed = match self.current().and_then(|current| {
            current.with_storage_node(address_hash, path, |node| probe(path, node, hash, &mut read))
        }) {
            Some(probed) => Some((probed?, ReadSource::Current)),
            None => self
                .own
                .load()
                .iter()
                .rev()
                .find_map(|snapshot| snapshot.try_get_storage_node(address_hash, path))
                .map(|node| probe(path, node, hash, &mut read))
                .transpose()?
                .map(|probed| (probed, ReadSource::Own)),
        };
        let read = read.take();
        match probed {
            Some((Probe::Found(value), source)) => Ok((Some(value), source)),
            Some((Probe::Placeholder, _)) => {
                Ok(self
                    .chain
                    .load_persisted_storage_rlp(address_hash, path, flags, unread(read))?)
            }
            None => self
                .chain
                .load_storage_rlp_checked(address_hash, path, hash, flags, unread(read)),
        }
    }

    /// Port of `SetStateNode`; empty `rlp` sets a hash-only placeholder, a zero `hash` an inline node.
    pub fn set_state_node(&self, path: &TreePath, hash: &Hash256, rlp: &[u8]) {
        self.current_or_create().set_state_node(path, hash, rlp);
    }

    /// Port of `PublishStateNodes`: sets every node of `records`, whose RLP lies in `blob`.
    pub fn put_state_nodes(&self, records: &[NodeRecord], blob: &[u8]) {
        self.current_or_create().put_state_nodes(records, blob);
    }

    /// Port of `SetStorageNode`.
    pub fn set_storage_node(&self, address_hash: &Hash256, path: &TreePath, hash: &Hash256, rlp: &[u8]) {
        self.current_or_create().set_storage_node(address_hash, path, hash, rlp);
    }

    /// Port of `PublishStorageNodes`.
    pub fn put_storage_nodes(&self, address_hash: &Hash256, records: &[NodeRecord], blob: &[u8]) {
        self.current_or_create().put_storage_nodes(address_hash, records, blob);
    }

    /// Port of the content part of `CollectAndApplySnapshot`: freezes the current content into a snapshot from `from`
    /// to `to` and appends it to the own snapshots, kept or not and empty or not, so the self-destruct indexes stay
    /// those of C#. Returns the snapshot when `keep`; a kept commit of main block processing moves the next content to
    /// `PostMainBlockProcessing`.
    pub fn commit(&self, from: StateId, to: StateId, keep: bool) -> Option<Arc<Snapshot>> {
        let mut locked = self.current_or_create().lock_all();
        let snapshot = Arc::new(Snapshot::new(from, to, locked.freeze()));
        let mut own = Vec::clone(&self.own.load());
        own.push(Arc::clone(&snapshot));
        self.own.store(Arc::new(own));
        locked.reset_written();
        if keep && self.usage() == Usage::MainBlockProcessing {
            self.usage
                .store(Usage::PostMainBlockProcessing as u8, Ordering::Relaxed);
            locked.set_usage(Usage::PostMainBlockProcessing);
        }
        drop(locked);
        keep.then_some(snapshot)
    }

    fn current(&self) -> Option<&MutableContent> {
        self.current.get().map(|current| &**current)
    }

    fn current_or_create(&self) -> &MutableContent {
        self.current
            .get_or_init(|| Box::new(MutableContent::new(Arc::clone(&self.stats), self.usage())))
    }

    /// `DoGetAccount` with `excludeChanged`: the own snapshots, then the chain.
    fn account_below_current(&self, address: &Address) -> PersistenceResult<(Option<Account>, ReadSource)> {
        if let Some(account) = self
            .own
            .load()
            .iter()
            .rev()
            .find_map(|snapshot| snapshot.try_get_account(address))
        {
            return Ok((account, ReadSource::Own));
        }
        self.chain.get_account(address)
    }
}

/// Checks the first in-memory node at `path` against the requested hash, reading it when it holds RLP.
fn probe<T>(
    path: &TreePath,
    node: TrieNodeRlp<'_>,
    hash: &Hash256,
    read: &mut Option<impl FnOnce(&[u8]) -> T>,
) -> Result<Probe<T>, NodeLoadError> {
    if node.hash != hash {
        return Err(hash_mismatch(path, node.hash, hash));
    }
    if node.is_hash_only_placeholder() {
        return Ok(Probe::Placeholder);
    }
    let read = read.take().expect("an in-memory node is read once");
    Ok(Probe::Found(read(node.rlp)))
}

/// The reader of a load whose in-memory probe did not read.
fn unread<F>(read: Option<F>) -> F {
    read.expect("a probe that did not read keeps the reader")
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::{Arc, Barrier};
    use std::thread;

    use super::{ScopeBundle, ScopeFlags};
    use crate::bundle::ReadOnlySnapshotBundle;
    use crate::kv::ReadFlags;
    use crate::persistence::NoopPersistenceReader;
    use crate::snapshot::mutable::Usage;
    use crate::stats::FlatStats;
    use crate::types::{Account, StateId, TreePath, U256};

    const WRITERS: u8 = 16;
    const READERS: usize = 8;
    const ROUNDS: u64 = 20_000;
    const SLOTS: u64 = 4;

    fn address(writer: u8) -> [u8; 20] {
        [writer; 20]
    }

    fn rlp(round: u64) -> Vec<u8> {
        [&[0xC8][..], &round.to_be_bytes()].concat()
    }

    /// 16 writers, one per address and shard, publish rising values while readers check that a value once written is
    /// never lost, through commits racing them; the lock-rank checker of this test build watches every lock taken.
    #[test]
    fn readers_never_miss_a_written_value_across_racing_commits() {
        let flags = ScopeFlags {
            filter_in_memory_slot_reads: false,
            usage: Usage::MainBlockProcessing,
        };
        let chain = Arc::new(ReadOnlySnapshotBundle::<NoopPersistenceReader>::empty());
        let scope = Arc::new(ScopeBundle::new(
            chain,
            Vec::new(),
            flags,
            Arc::new(FlatStats::default()),
        ));
        let progress: Arc<[AtomicU64]> = (0..WRITERS).map(|_| AtomicU64::new(0)).collect();
        let done = Arc::new(AtomicBool::new(false));
        let start = Arc::new(Barrier::new(usize::from(WRITERS) + READERS + 1));
        let path = TreePath::from_hex("12");

        let writers: Vec<_> = (0..WRITERS)
            .map(|writer| {
                let (scope, progress, start) = (Arc::clone(&scope), Arc::clone(&progress), Arc::clone(&start));
                thread::spawn(move || {
                    start.wait();
                    let (address, address_hash) = (address(writer), [writer; 32]);
                    for round in 1..=ROUNDS {
                        scope.set_account(&address, Some(Account::new(round, U256::ZERO)));
                        scope.set_slot(&address, &U256::from_u64(round % SLOTS), &U256::from_u64(round));
                        scope.set_storage_node(&address_hash, &path, &address_hash, &rlp(round));
                        if writer == 0 {
                            scope.set_state_node(&path, &[0; 32], &rlp(round));
                        }
                        progress[usize::from(writer)].store(round, Ordering::Release);
                    }
                })
            })
            .collect();

        let readers: Vec<_> = (0..READERS)
            .map(|reader| {
                let (scope, progress, done, start) = (
                    Arc::clone(&scope),
                    Arc::clone(&progress),
                    Arc::clone(&done),
                    Arc::clone(&start),
                );
                thread::spawn(move || {
                    start.wait();
                    let mut checks = 0u64;
                    let mut writer = reader as u8;
                    while !done.load(Ordering::Acquire) {
                        writer = (writer + 1) % WRITERS;
                        let written = progress[usize::from(writer)].load(Ordering::Acquire);
                        if written == 0 {
                            continue;
                        }
                        let address = address(writer);
                        let (account, _) = scope.get_account(&address, false).unwrap();
                        assert!(
                            account.is_some_and(|account| account.nonce >= written),
                            "account of {writer}"
                        );
                        let (slot, _) = scope.get_slot(&address, &U256::from_u64(written % SLOTS), -1).unwrap();
                        assert!(slot.is_some_and(|value| value.0[0] >= written), "slot of {writer}");
                        let (node, _) = scope
                            .load_storage_rlp(&[writer; 32], &path, &[writer; 32], ReadFlags::NONE, <[u8]>::to_vec)
                            .unwrap();
                        let round = u64::from_be_bytes(node.expect("a written node")[1..].try_into().unwrap());
                        assert!(round >= written, "storage node of {writer}");
                        checks += 1;
                    }
                    checks
                })
            })
            .collect();

        start.wait();
        let mut commits = 0u64;
        while progress.iter().any(|written| written.load(Ordering::Acquire) < ROUNDS) {
            scope.commit(
                StateId::new(commits, [0; 32]),
                StateId::new(commits + 1, [0; 32]),
                commits.is_multiple_of(2),
            );
            commits += 1;
            thread::yield_now();
        }
        writers.into_iter().for_each(|writer| writer.join().unwrap());
        done.store(true, Ordering::Release);
        let checks: u64 = readers.into_iter().map(|reader| reader.join().unwrap()).sum();
        scope.commit(StateId::new(commits, [0; 32]), StateId::new(commits + 1, [0; 32]), true);

        assert!(
            commits > 10 && checks > 1_000,
            "{commits} commits raced {checks} checks"
        );
        for writer in 0..WRITERS {
            let (account, _) = scope.get_account(&address(writer), false).unwrap();
            assert_eq!(account.map(|account| account.nonce), Some(ROUNDS));
        }
        let (state_node, _) = scope
            .load_state_rlp(&path, &[0; 32], ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap();
        assert_eq!(state_node, Some(rlp(ROUNDS)));
    }
}
