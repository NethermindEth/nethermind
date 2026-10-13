//! The content a scope writes: random single and batched writes frozen by [`MutableContent::freeze`] hold exactly what
//! the same writes give through [`SnapshotContentBuilder::build`], and the pooled-resource gauges count a content from
//! its first write until the last snapshot holding it drops.

mod common;

use std::sync::Arc;
use std::sync::atomic::Ordering;

use common::hex;
use common::scope_model::{
    ADDRESSES, HASHES, Layer, ModelAccount, PATHS, SLOTS, account_strategy, model_account, model_address,
    model_address_hash, model_hash, model_path, model_slot,
};
use common::snapshots::state_id;
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::mutable::{MutableContent, NodeRecord, Usage};
use flatdb_core::stats::FlatStats;
use flatdb_core::types::{TreePath, U256};
use proptest::prelude::*;
use rstest::rstest;

/// A write to a content.
#[derive(Clone, Debug)]
enum Write {
    SetAccount(u8, Option<ModelAccount>),
    Promote(u8, Option<ModelAccount>),
    SetSlot(u8, u8, u64),
    SelfDestruct(u8, bool),
    RemoveStorage(u8),
    StateNode(u8, u8, Vec<u8>),
    StateNodes(Vec<(u8, u8, Vec<u8>)>),
    StorageNode(u8, u8, u8, Vec<u8>),
    StorageNodes(u8, Vec<(u8, u8, Vec<u8>)>),
}

fn node() -> impl Strategy<Value = (u8, u8, Vec<u8>)> {
    (
        0..PATHS.len() as u8,
        0..HASHES,
        prop_oneof![1 => Just(Vec::new()), 3 => prop::collection::vec(any::<u8>(), 1..40)],
    )
}

fn write_strategy() -> impl Strategy<Value = Write> {
    let address = || 0..ADDRESSES;
    prop_oneof![
        (address(), prop::option::of(account_strategy())).prop_map(|(a, account)| Write::SetAccount(a, account)),
        (address(), prop::option::of(account_strategy())).prop_map(|(a, account)| Write::Promote(a, account)),
        (address(), 0..SLOTS, prop_oneof![Just(0u64), 1u64..100]).prop_map(|(a, k, v)| Write::SetSlot(a, k, v)),
        (address(), any::<bool>()).prop_map(|(a, is_new)| Write::SelfDestruct(a, is_new)),
        address().prop_map(Write::RemoveStorage),
        node().prop_map(|(p, h, rlp)| Write::StateNode(p, h, rlp)),
        prop::collection::vec(node(), 0..5).prop_map(Write::StateNodes),
        (address(), node()).prop_map(|(a, (p, h, rlp))| Write::StorageNode(a, p, h, rlp)),
        (address(), prop::collection::vec(node(), 0..5)).prop_map(|(a, nodes)| Write::StorageNodes(a, nodes)),
    ]
}

/// The records of `nodes` over one blob, as a committer publishes a buffer.
fn records(nodes: &[(u8, u8, Vec<u8>)]) -> (Vec<NodeRecord>, Vec<u8>) {
    let mut blob = Vec::new();
    let records = nodes
        .iter()
        .map(|(p, h, rlp)| {
            let record = NodeRecord {
                path: model_path(*p),
                hash: model_hash(*h),
                rlp_offset: blob.len(),
                rlp_len: rlp.len(),
            };
            blob.extend_from_slice(rlp);
            record
        })
        .collect();
    (records, blob)
}

/// Applies `write` to the content and to the model of what it should freeze into.
fn apply(content: &MutableContent, model: &mut Layer, write: &Write) {
    match write.clone() {
        Write::SetAccount(a, account) => {
            content.set_account(&model_address(a), account.map(model_account));
            model.accounts.insert(a, account);
        }
        Write::Promote(a, account) => content.promote_account(&model_address(a), account.map(model_account)),
        Write::SetSlot(a, k, v) => {
            content.set_slot(&model_address(a), &model_slot(k), &U256::from_u64(v));
            model.slots.insert((a, k), (v != 0).then_some(v));
        }
        Write::SelfDestruct(a, is_new) => {
            let added = content.set_self_destruct_if_absent(&model_address(a), is_new);
            assert_eq!(added, !model.clears.contains_key(&a));
            model.clears.entry(a).or_insert(is_new);
        }
        Write::RemoveStorage(a) => {
            content.remove_storage(&model_address(a), &model_address_hash(a));
            model.slots.retain(|(address, _), _| *address != a);
            model.storage_nodes.retain(|(address, _), _| *address != a);
        }
        Write::StateNode(p, h, rlp) => {
            content.set_state_node(&model_path(p), &model_hash(h), &rlp);
            model.state_nodes.insert(p, (h, rlp));
        }
        Write::StateNodes(nodes) => {
            let (records, blob) = records(&nodes);
            content.put_state_nodes(&records, &blob);
            model
                .state_nodes
                .extend(nodes.into_iter().map(|(p, h, rlp)| (p, (h, rlp))));
        }
        Write::StorageNode(a, p, h, rlp) => {
            content.set_storage_node(&model_address_hash(a), &model_path(p), &model_hash(h), &rlp);
            model.storage_nodes.insert((a, p), (h, rlp));
        }
        Write::StorageNodes(a, nodes) => {
            let (records, blob) = records(&nodes);
            content.put_storage_nodes(&model_address_hash(a), &records, &blob);
            model
                .storage_nodes
                .extend(nodes.into_iter().map(|(p, h, rlp)| ((a, p), (h, rlp))));
        }
    }
}

/// The reads of the content before it is frozen match the model, the read cache aside.
fn check_reads(content: &MutableContent, model: &Layer) -> Result<(), TestCaseError> {
    for a in 0..ADDRESSES {
        let address = model_address(a);
        if let Some(account) = model.accounts.get(&a) {
            prop_assert_eq!(content.account(&address), Some(account.map(model_account)));
        }
        prop_assert_eq!(content.self_destruct(&address), model.clears.get(&a).copied());
        for k in 0..SLOTS {
            let expected = model.slots.get(&(a, k)).map(|value| value.map(U256::from_u64));
            prop_assert_eq!(content.slot(&address, &model_slot(k)), expected);
        }
        for p in 0..PATHS.len() as u8 {
            let read = content.with_storage_node(&model_address_hash(a), &model_path(p), |node| {
                (node.hash[0], node.rlp.to_vec())
            });
            prop_assert_eq!(read.as_ref(), model.storage_nodes.get(&(a, p)));
        }
    }
    for p in 0..PATHS.len() as u8 {
        let read = content.with_state_node(&model_path(p), |node| (node.hash[0], node.rlp.to_vec()));
        prop_assert_eq!(read.as_ref(), model.state_nodes.get(&p));
    }
    Ok(())
}

/// The model built the way `SnapshotContentBuilder` assembles a block.
fn built(model: &Layer) -> Arc<Snapshot> {
    model.snapshot(state_id(0, 0), state_id(1, 0))
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(512))]

    /// Two blocks through one content, so the second starts from a frozen, emptied one.
    #[test]
    fn a_frozen_content_holds_what_the_builder_builds(
        blocks in prop::collection::vec(prop::collection::vec(write_strategy(), 0..40), 2),
    ) {
        let mut content = MutableContent::new(Arc::new(FlatStats::default()), Usage::MainBlockProcessing);
        for writes in &blocks {
            let mut model = Layer::default();
            for write in writes {
                apply(&content, &mut model, write);
            }
            check_reads(&content, &model)?;

            let frozen = Snapshot::new(state_id(0, 0), state_id(1, 0), content.freeze());
            let expected = built(&model);
            prop_assert_eq!(Layer::of(&frozen), Layer::of(&expected));
            prop_assert_eq!(frozen.counts(), expected.counts());
            prop_assert_eq!(content.account(&model_address(0)), None, "a frozen content is empty");
        }
    }
}

/// Nodes beyond a chunk, and many filling several, keep their bytes through the freeze.
#[test]
fn large_and_many_nodes_keep_their_rlp() {
    let mut content = MutableContent::new(Arc::new(FlatStats::default()), Usage::MainBlockProcessing);
    let large = vec![0xAB; 40_000];
    let mut builder = SnapshotContentBuilder::new();
    content.set_state_node(&TreePath::from_hex("1"), &[1; 32], &large);
    builder.set_state_node(TreePath::from_hex("1"), [1; 32], &large);
    for index in 0u32..5_000 {
        let address_hash = model_address_hash((index % 6) as u8);
        let mut path_bytes = [0u8; 32];
        path_bytes[..4].copy_from_slice(&index.to_be_bytes());
        let path = TreePath::new(path_bytes, 8);
        let rlp = hex(&format!("0xc4{index:08x}"));
        content.set_storage_node(&address_hash, &path, &[2; 32], &rlp);
        builder.set_storage_node(address_hash, path, [2; 32], &rlp);
    }

    let frozen = Snapshot::new(state_id(0, 0), state_id(1, 0), content.freeze());
    let expected = Snapshot::new(state_id(0, 0), state_id(1, 0), builder.build());
    let nodes = |snapshot: &Snapshot| {
        let mut nodes: Vec<(Vec<u8>, Vec<u8>)> = snapshot
            .storage_nodes()
            .map(|(key, node)| ([&key.address_hash[..], &key.path.path[..4]].concat(), node.rlp.to_vec()))
            .collect();
        nodes.sort();
        nodes
    };
    assert_eq!(nodes(&frozen), nodes(&expected));
    assert_eq!(
        frozen.try_get_state_node(&TreePath::from_hex("1")).unwrap().rlp,
        &large[..]
    );
}

/// `ActivePooledResource`/`CreatedPooledResource` of `SnapshotContent`: a content is live from its first write, or from
/// the freeze of an unwritten one, until the last snapshot holding it drops, under the usage it was written with.
#[rstest]
fn a_content_counts_live_from_its_first_write_until_its_snapshot_drops(
    #[values(Usage::MainBlockProcessing, Usage::ReadOnlyProcessingEnv)] usage: Usage,
) {
    let stats = Arc::new(FlatStats::default());
    let gauges = || {
        let index = usage as usize;
        let content = &stats.counters.content;
        (
            content.active[index].load(Ordering::Relaxed),
            content.created[index].load(Ordering::Relaxed),
        )
    };
    let mut content = MutableContent::new(Arc::clone(&stats), usage);
    content.put_state_nodes(&[], &[]);
    content.put_storage_nodes(&model_address_hash(1), &[], &[]);
    assert_eq!(
        gauges(),
        (0, 0),
        "an unwritten content, or one given only empty batches, is not counted"
    );

    content.set_slot(&model_address(1), &model_slot(1), &U256::from_u64(5));
    content.promote_account(&model_address(2), None);
    assert_eq!(gauges(), (1, 1));

    let written = Snapshot::new(state_id(0, 0), state_id(1, 0), content.freeze());
    let unwritten = Snapshot::new(state_id(1, 0), state_id(2, 0), content.freeze());
    assert_eq!(gauges(), (2, 2), "freezing an unwritten content creates one");

    content.set_account(&model_address(3), None);
    drop(written);
    drop(unwritten);
    assert_eq!(gauges(), (1, 3));
    drop(content);
    assert_eq!(gauges(), (0, 3), "a dropped content with writes is released");
}
