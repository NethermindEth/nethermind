//! A scope's bundle against a plain transcription of the C# reads it ports (`SnapshotBundle.cs` `DoGetAccount`,
//! `DetermineSelfDestructSnapshotIdx`, `GetSlot`, `ClearStorage`, `CollectAndApplySnapshot` and the
//! `ReadOnlySnapshotBundle.cs` reads below them), with the checked loads of the scope's trie adapters and the unchecked
//! loads of the read-only ones; and the named cases of those rules.

mod common;

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use common::scope_model::{
    ADDRESSES, HASHES, Layer, ModelAccount, PATHS, Persisted, SLOTS, account_strategy, layer_strategy, model_account,
    model_address, model_address_hash, model_hash, model_path, model_slot, persisted_strategy,
};
use common::snapshots::{DictionaryReader, state_id};
use flatdb_core::bundle::{NodeLoadError, ReadOnlySnapshotBundle, ReadSource};
use flatdb_core::kv::ReadFlags;
use flatdb_core::persistence::PersistenceResult;
use flatdb_core::scope_bundle::{ScopeBundle, ScopeFlags};
use flatdb_core::snapshot::mutable::Usage;
use flatdb_core::stats::FlatStats;
use flatdb_core::types::{Account, U256};
use proptest::prelude::*;
use proptest::test_runner::TestCaseError;
use rstest::rstest;

const BITS_PER_KEY: f64 = 14.0;

#[derive(Clone, Debug)]
enum SelfDestructIndex {
    /// The index the scope reports now.
    Fresh,
    /// One the scope reported earlier, as a caller holding it across writes and commits passes it.
    Stale(usize),
    None,
}

#[derive(Clone, Debug)]
enum Op {
    SetAccount(u8, Option<ModelAccount>),
    GetAccount(u8, bool),
    Promote(u8, Option<ModelAccount>),
    SetSlot(u8, u8, u64),
    GetSlot(u8, u8, SelfDestructIndex),
    SelfDestructIdx(u8),
    ClearStorage(u8),
    StateNode(u8, u8, Vec<u8>),
    StorageNode(u8, u8, u8, Vec<u8>),
    LoadState(u8, u8),
    LoadStorage(u8, u8, u8),
    LoadUnchecked(u8),
    Commit(bool),
}

fn op_strategy() -> impl Strategy<Value = Op> {
    let address = || 0..ADDRESSES;
    let path = || 0..PATHS.len() as u8;
    let rlp = || prop_oneof![1 => Just(Vec::new()), 3 => prop::collection::vec(any::<u8>(), 1..4)];
    let index = prop_oneof![
        2 => Just(SelfDestructIndex::Fresh),
        2 => any::<usize>().prop_map(SelfDestructIndex::Stale),
        1 => Just(SelfDestructIndex::None),
    ];
    prop_oneof![
        3 => (address(), prop::option::of(account_strategy())).prop_map(|(a, account)| Op::SetAccount(a, account)),
        4 => (address(), any::<bool>()).prop_map(|(a, promote)| Op::GetAccount(a, promote)),
        1 => (address(), prop::option::of(account_strategy())).prop_map(|(a, account)| Op::Promote(a, account)),
        3 => (address(), 0..SLOTS, prop_oneof![Just(0u64), 1u64..100]).prop_map(|(a, k, v)| Op::SetSlot(a, k, v)),
        6 => (address(), 0..SLOTS, index).prop_map(|(a, k, index)| Op::GetSlot(a, k, index)),
        2 => address().prop_map(Op::SelfDestructIdx),
        2 => address().prop_map(Op::ClearStorage),
        2 => (path(), 0..HASHES, rlp()).prop_map(|(p, h, rlp)| Op::StateNode(p, h, rlp)),
        2 => (address(), path(), 0..HASHES, rlp()).prop_map(|(a, p, h, rlp)| Op::StorageNode(a, p, h, rlp)),
        3 => (path(), 0..HASHES).prop_map(|(p, h)| Op::LoadState(p, h)),
        3 => (address(), path(), 0..HASHES).prop_map(|(a, p, h)| Op::LoadStorage(a, p, h)),
        1 => path().prop_map(Op::LoadUnchecked),
        2 => any::<bool>().prop_map(Op::Commit),
    ]
}

/// What a checked load gives: the RLP and where it came from, or a hash mismatch.
type Loaded = Result<(Option<Vec<u8>>, ReadSource), ()>;

/// The C# reads over plain layers: the chain's snapshots, the own ones, the current content and its read cache.
struct Model {
    persisted: Persisted,
    chain: Vec<Layer>,
    own: Vec<Layer>,
    current: Layer,
    read: BTreeMap<u8, Option<ModelAccount>>,
}

impl Model {
    /// `DoGetAccount`.
    fn account(&self, a: u8, exclude_current: bool) -> (Option<ModelAccount>, ReadSource) {
        if !exclude_current && let Some(account) = self.current.accounts.get(&a).or_else(|| self.read.get(&a)) {
            return (*account, ReadSource::Current);
        }
        if let Some(account) = self.own.iter().rev().find_map(|layer| layer.accounts.get(&a)) {
            return (*account, ReadSource::Own);
        }
        if let Some(account) = self.chain.iter().rev().find_map(|layer| layer.accounts.get(&a)) {
            return (*account, ReadSource::Snapshot);
        }
        (self.persisted.accounts.get(&a).copied(), ReadSource::Persistence)
    }

    /// `PromoteAccount`.
    fn promote(&mut self, a: u8, account: Option<ModelAccount>) {
        if !self.current.accounts.contains_key(&a) && !self.read.contains_key(&a) {
            self.read.insert(a, account);
        }
    }

    /// `DetermineSelfDestructSnapshotIdx`.
    fn self_destruct_idx(&self, a: u8) -> i32 {
        let chain_count = self.chain.len() as i32;
        if self.current.clears.contains_key(&a) {
            return chain_count + self.own.len() as i32;
        }
        if let Some(index) = self.own.iter().rposition(|layer| layer.clears.contains_key(&a)) {
            return chain_count + index as i32;
        }
        self.chain
            .iter()
            .rposition(|layer| layer.clears.contains_key(&a))
            .map_or(-1, |index| index as i32)
    }

    /// `SnapshotBundle.GetSlot` over `ReadOnlySnapshotBundle.GetSlot`.
    fn slot(&self, a: u8, k: u8, cleared: i32) -> (Option<u64>, ReadSource) {
        if let Some(value) = self.current.slots.get(&(a, k)) {
            return (*value, ReadSource::Current);
        }
        let (chain_count, cleared) = (self.chain.len() as i64, i64::from(cleared));
        if cleared == chain_count + self.own.len() as i64 {
            return (None, ReadSource::SelfDestructed);
        }
        for (index, layer) in self.own.iter().enumerate().rev() {
            if let Some(value) = layer.slots.get(&(a, k)) {
                return (*value, ReadSource::Own);
            }
            if index as i64 <= cleared - chain_count {
                return (None, ReadSource::SelfDestructed);
            }
        }
        for (index, layer) in self.chain.iter().enumerate().rev() {
            if let Some(value) = layer.slots.get(&(a, k)) {
                return (*value, ReadSource::Snapshot);
            }
            if index as i64 <= cleared {
                return (None, ReadSource::SelfDestructed);
            }
        }
        (self.persisted.slots.get(&(a, k)).copied(), ReadSource::Persistence)
    }

    /// `ClearStorage`.
    fn clear_storage(&mut self, a: u8) {
        let (account, _) = self.account(a, true);
        let is_new_account = account.is_none_or(|(_, has_storage)| !has_storage);
        self.current.clears.entry(a).or_insert(is_new_account);
        self.current.slots.retain(|(address, _), _| *address != a);
        self.current.storage_nodes.retain(|(address, _), _| *address != a);
    }

    /// The first node at the key in the current content, the own snapshots, then the chain, checked against `hash`.
    fn load_checked(
        &self,
        find: impl Fn(&Layer) -> Option<&(u8, Vec<u8>)>,
        hash: u8,
        persisted: Option<&Vec<u8>>,
    ) -> Loaded {
        let layers = std::iter::once((&self.current, ReadSource::Current))
            .chain(self.own.iter().rev().map(|layer| (layer, ReadSource::Own)))
            .chain(self.chain.iter().rev().map(|layer| (layer, ReadSource::Snapshot)));
        for (layer, source) in layers {
            if let Some((stored, rlp)) = find(layer) {
                if *stored != hash {
                    return Err(());
                }
                if !rlp.is_empty() {
                    return Ok((Some(rlp.clone()), source));
                }
                break;
            }
        }
        Ok((persisted.cloned(), ReadSource::Persistence))
    }

    /// The read-only adapter's load: the chain's first node at the path, unchecked.
    fn load_unchecked(&self, p: u8) -> (Option<Vec<u8>>, ReadSource) {
        match self.chain.iter().rev().find_map(|layer| layer.state_nodes.get(&p)) {
            Some((_, rlp)) if !rlp.is_empty() => (Some(rlp.clone()), ReadSource::Snapshot),
            _ => (self.persisted.state_nodes.get(&p).cloned(), ReadSource::Persistence),
        }
    }
}

fn to_model(account: Option<Account>) -> Option<ModelAccount> {
    account.map(|account| (account.nonce, account.has_storage()))
}

fn slot_value((value, source): (Option<U256>, ReadSource)) -> (Option<u64>, ReadSource) {
    (value.map(|value| value.0[0]), source)
}

fn loaded(result: Result<(Option<Vec<u8>>, ReadSource), NodeLoadError>) -> Result<Loaded, TestCaseError> {
    match result {
        Ok(loaded) => Ok(Ok(loaded)),
        Err(NodeLoadError::HashMismatch { .. }) => Ok(Err(())),
        Err(error) => Err(TestCaseError::fail(format!("{error}"))),
    }
}

/// A scope over the chain `chain`, seeded with `own`, above `persisted`.
fn scope(
    persisted: &Persisted,
    chain: &[Layer],
    own: &[Layer],
    flags: ScopeFlags,
    stats: Arc<FlatStats>,
) -> ScopeBundle<DictionaryReader> {
    let snapshots = |layers: &[Layer], first_block: u64| {
        layers
            .iter()
            .zip(first_block..)
            .map(|(layer, block)| layer.snapshot(state_id(block, 0), state_id(block + 1, 0)))
            .collect::<Vec<_>>()
    };
    let chain_bundle = ReadOnlySnapshotBundle::new(snapshots(chain, 0), persisted.reader(), BITS_PER_KEY);
    ScopeBundle::new(Arc::new(chain_bundle), snapshots(own, chain.len() as u64), flags, stats)
}

fn run(
    persisted: Persisted,
    chain: Vec<Layer>,
    own: Vec<Layer>,
    flags: ScopeFlags,
    ops: &[Op],
) -> Result<(), TestCaseError> {
    let bundle = scope(&persisted, &chain, &own, flags, Arc::new(FlatStats::default()));
    let mut model = Model {
        persisted,
        chain,
        own,
        current: Layer::default(),
        read: BTreeMap::new(),
    };
    let mut reported_indexes = Vec::new();
    let mut block = (model.chain.len() + model.own.len()) as u64;

    for op in ops {
        match op.clone() {
            Op::SetAccount(a, account) => {
                bundle.set_account(&model_address(a), account.map(model_account));
                model.current.accounts.insert(a, account);
            }
            Op::GetAccount(a, promote) => {
                let (account, source) = bundle.get_account(&model_address(a), promote).unwrap();
                let expected = model.account(a, false);
                prop_assert_eq!((to_model(account), source), expected, "{:?}", op);
                if promote && expected.1 != ReadSource::Current {
                    model.promote(a, expected.0);
                }
            }
            Op::Promote(a, account) => {
                bundle.promote_account(&model_address(a), account.map(model_account));
                model.promote(a, account);
            }
            Op::SetSlot(a, k, v) => {
                bundle.set_slot(&model_address(a), &model_slot(k), &U256::from_u64(v));
                model.current.slots.insert((a, k), (v != 0).then_some(v));
            }
            Op::GetSlot(a, k, index) => {
                let cleared = match index {
                    SelfDestructIndex::Fresh => model.self_destruct_idx(a),
                    SelfDestructIndex::Stale(pick) if !reported_indexes.is_empty() => {
                        reported_indexes[pick % reported_indexes.len()]
                    }
                    _ => -1,
                };
                let read = bundle.get_slot(&model_address(a), &model_slot(k), cleared).unwrap();
                prop_assert_eq!(slot_value(read), model.slot(a, k, cleared), "{:?} at {}", op, cleared);
            }
            Op::SelfDestructIdx(a) => {
                let index = bundle.self_destruct_idx(&model_address(a));
                prop_assert_eq!(index, model.self_destruct_idx(a), "{:?}", op);
                reported_indexes.push(index);
            }
            Op::ClearStorage(a) => {
                bundle.clear_storage(&model_address(a), &model_address_hash(a)).unwrap();
                model.clear_storage(a);
            }
            Op::StateNode(p, h, rlp) => {
                bundle.set_state_node(&model_path(p), &model_hash(h), &rlp);
                model.current.state_nodes.insert(p, (h, rlp));
            }
            Op::StorageNode(a, p, h, rlp) => {
                bundle.set_storage_node(&model_address_hash(a), &model_path(p), &model_hash(h), &rlp);
                model.current.storage_nodes.insert((a, p), (h, rlp));
            }
            Op::LoadState(p, h) => {
                let actual = bundle.load_state_rlp(&model_path(p), &model_hash(h), ReadFlags::NONE, <[u8]>::to_vec);
                let expected = model.load_checked(
                    |layer| layer.state_nodes.get(&p),
                    h,
                    model.persisted.state_nodes.get(&p),
                );
                prop_assert_eq!(loaded(actual)?, expected, "{:?}", op);
            }
            Op::LoadStorage(a, p, h) => {
                let actual = bundle.load_storage_rlp(
                    &model_address_hash(a),
                    &model_path(p),
                    &model_hash(h),
                    ReadFlags::NONE,
                    <[u8]>::to_vec,
                );
                let expected = model.load_checked(
                    |layer| layer.storage_nodes.get(&(a, p)),
                    h,
                    model.persisted.storage_nodes.get(&(a, p)),
                );
                prop_assert_eq!(loaded(actual)?, expected, "{:?}", op);
            }
            Op::LoadUnchecked(p) => {
                let actual = bundle
                    .chain()
                    .load_state_rlp_unchecked(&model_path(p), ReadFlags::NONE, <[u8]>::to_vec)
                    .unwrap();
                prop_assert_eq!(actual, model.load_unchecked(p), "{:?}", op);
            }
            Op::Commit(keep) => {
                let committed = bundle.commit(state_id(block, 1), state_id(block + 1, 1), keep);
                block += 1;
                let frozen = std::mem::take(&mut model.current);
                model.read.clear();
                prop_assert_eq!(committed.is_some(), keep);
                if let Some(snapshot) = committed {
                    prop_assert_eq!(Layer::of(&snapshot), frozen.clone());
                }
                model.own.push(frozen);
                prop_assert_eq!(bundle.own_count(), model.own.len());
            }
        }
    }
    Ok(())
}

fn flags_strategy() -> impl Strategy<Value = ScopeFlags> {
    (
        any::<bool>(),
        prop_oneof![
            Just(Usage::MainBlockProcessing),
            Just(Usage::PostMainBlockProcessing),
            Just(Usage::ReadOnlyProcessingEnv)
        ],
    )
        .prop_map(|(filter_in_memory_slot_reads, usage)| ScopeFlags {
            filter_in_memory_slot_reads,
            usage,
        })
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(512))]

    #[test]
    fn a_scope_reads_as_the_csharp_bundle_over_the_same_layers(
        persisted in persisted_strategy(),
        chain in prop::collection::vec(layer_strategy(), 0..5),
        own in prop::collection::vec(layer_strategy(), 0..3),
        flags in flags_strategy(),
        ops in prop::collection::vec(op_strategy(), 0..80),
    ) {
        run(persisted, chain, own, flags, &ops)?;
    }
}

fn layer(fill: impl FnOnce(&mut Layer)) -> Layer {
    let mut layer = Layer::default();
    fill(&mut layer);
    layer
}

/// Where the first node at a path sits in a scope with a node of hash 1 at path 1 in every layer.
#[derive(Clone, Copy, Debug)]
enum Holder {
    Current,
    Own,
    Chain,
}

/// The first node at a path decides a checked load: another hash is a mismatch even under a matching older node, a
/// placeholder of the hash reads persistence past every older node; the read-only adapter's load returns the chain's
/// node without comparing.
#[rstest]
fn the_first_node_at_a_path_decides_a_checked_load(
    #[values(Holder::Current, Holder::Own, Holder::Chain)] holder: Holder,
    #[values(true, false)] placeholder: bool,
) {
    let persisted = Persisted {
        state_nodes: BTreeMap::from([(1, vec![0xC0])]),
        ..Persisted::default()
    };
    let first_rlp = if placeholder { Vec::new() } else { vec![0xC1, 0x02] };
    let older = layer(|l| {
        l.state_nodes.insert(1, (1, vec![0xC1, 0x01]));
    });
    let first = layer(|l| {
        l.state_nodes.insert(1, (2, first_rlp.clone()));
    });
    let (chain, own) = match holder {
        Holder::Chain => (vec![older.clone(), first.clone()], vec![]),
        Holder::Own => (vec![older.clone()], vec![first.clone()]),
        Holder::Current => (vec![older.clone()], vec![older.clone()]),
    };
    let flags = ScopeFlags {
        filter_in_memory_slot_reads: false,
        usage: Usage::MainBlockProcessing,
    };
    let bundle = scope(&persisted, &chain, &own, flags, Arc::new(FlatStats::default()));
    if let Holder::Current = holder {
        bundle.set_state_node(&model_path(1), &model_hash(2), &first_rlp);
    }
    let load = |hash: u8| bundle.load_state_rlp(&model_path(1), &model_hash(hash), ReadFlags::NONE, <[u8]>::to_vec);

    assert!(matches!(load(1), Err(NodeLoadError::HashMismatch { .. })));
    let source = match holder {
        Holder::Current => ReadSource::Current,
        Holder::Own => ReadSource::Own,
        Holder::Chain => ReadSource::Snapshot,
    };
    let expected = if placeholder {
        (Some(vec![0xC0]), ReadSource::Persistence)
    } else {
        (Some(first_rlp.clone()), source)
    };
    assert_eq!(load(2).unwrap(), expected);

    let unchecked = bundle
        .chain()
        .load_state_rlp_unchecked(&model_path(1), ReadFlags::NONE, <[u8]>::to_vec)
        .unwrap();
    let chain_first = match holder {
        Holder::Chain if placeholder => (Some(vec![0xC0]), ReadSource::Persistence),
        Holder::Chain => (Some(first_rlp), ReadSource::Snapshot),
        _ => (Some(vec![0xC1, 0x01]), ReadSource::Snapshot),
    };
    assert_eq!(unchecked, chain_first, "the read-only load never compares hashes");
}

/// A commit appends to the own snapshots kept or not, so a clear committed without keeping is still found at its
/// index after the chain; only a kept commit of main block processing moves the next content to post-main.
#[rstest]
fn commits_append_kept_or_not_and_a_kept_main_commit_moves_to_post_main(
    #[values(Usage::MainBlockProcessing, Usage::ReadOnlyProcessingEnv)] usage: Usage,
) -> PersistenceResult<()> {
    let stats = Arc::new(FlatStats::default());
    let chain = vec![Layer::default(), Layer::default()];
    let flags = ScopeFlags {
        filter_in_memory_slot_reads: true,
        usage,
    };
    let bundle = scope(&Persisted::default(), &chain, &[], flags, Arc::clone(&stats));
    let (a, slot) = (model_address(1), model_slot(1));

    bundle.set_slot(&a, &slot, &U256::from_u64(7));
    bundle.clear_storage(&a, &model_address_hash(1))?;
    assert_eq!(bundle.self_destruct_idx(&a), 2, "the current content is the tip");
    assert!(bundle.commit(state_id(2, 0), state_id(3, 0), false).is_none());
    assert_eq!(
        bundle.self_destruct_idx(&a),
        2,
        "the uncommitted clear is own snapshot 0"
    );
    assert_eq!(bundle.get_slot(&a, &slot, 2)?, (None, ReadSource::SelfDestructed));

    bundle.set_slot(&a, &slot, &U256::from_u64(9));
    let kept = bundle.commit(state_id(3, 0), state_id(4, 0), true).unwrap();
    assert_eq!(kept.try_get_storage(&a, &slot), Some(Some(U256::from_u64(9))));
    assert_eq!(
        bundle.get_slot(&a, &slot, 2)?,
        (Some(U256::from_u64(9)), ReadSource::Own)
    );
    assert_eq!(bundle.own_count(), 2);

    let next_usage = match usage {
        Usage::MainBlockProcessing => Usage::PostMainBlockProcessing,
        other => other,
    };
    assert_eq!(bundle.usage(), next_usage);
    bundle.set_account(&a, None);
    let active = |usage: Usage| stats.content.active[usage as usize].load(Ordering::Relaxed);
    assert_eq!(
        (active(usage), active(next_usage)),
        if usage == next_usage { (3, 3) } else { (2, 1) },
        "two frozen contents and the one being written"
    );
    assert!(bundle.filters_slot_reads(), "a chain of two snapshots builds a filter");
    Ok(())
}

/// Whether a clear sits below the scope: the chain's or the own seed's, a new account's included; the C# shell skips
/// the self-destruct lookup when there is none, so it must agree with the index the scope reports.
#[rstest]
#[case::in_the_chain(2, Some(0), None, true)]
#[case::of_a_new_account_in_the_chain(2, Some(1), None, true)]
#[case::in_the_own_seed(2, None, Some(0), true)]
#[case::in_the_own_seed_over_an_empty_chain(0, None, Some(0), true)]
#[case::nowhere(2, None, None, false)]
#[case::nowhere_over_an_empty_chain(0, None, None, false)]
fn a_scope_may_have_self_destructs_exactly_when_a_clear_is_below_it(
    #[case] chain_length: usize,
    #[case] chain_clear: Option<usize>,
    #[case] own_clear: Option<usize>,
    #[case] expected: bool,
) {
    let cleared = |clear_at: Option<usize>, length: usize| {
        (0..length)
            .map(|index| {
                layer(|l| {
                    if clear_at == Some(index) {
                        l.clears.insert(1, index == 1);
                    }
                })
            })
            .collect::<Vec<_>>()
    };
    let flags = ScopeFlags {
        filter_in_memory_slot_reads: false,
        usage: Usage::MainBlockProcessing,
    };
    let bundle = scope(
        &Persisted::default(),
        &cleared(chain_clear, chain_length),
        &cleared(own_clear, 1),
        flags,
        Arc::new(FlatStats::default()),
    );

    assert_eq!(bundle.may_have_self_destructs(), expected);
    assert_eq!(bundle.self_destruct_idx(&model_address(1)) != -1, expected);
}
