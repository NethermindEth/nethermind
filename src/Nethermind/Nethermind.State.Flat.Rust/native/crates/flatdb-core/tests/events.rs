//! The persisted node queue with long finality: a conversion into the persisted-snapshot tier queues the trie nodes
//! each base wrote, which C# marks persisted while `PersistedSnapshotBuilder` extracts them, and a persist from the tier
//! queues an entry without nodes; both kinds share the one queue, in the order they happened.

mod common;

use std::sync::Arc;

use common::any_kv::Backend;
use common::manager::{BLOCK0, Fixture, PM_CONFIG_LF, state};
use common::snapshots::snapshot;
use flatdb_core::events::{EventKind, PersistedNode, PersistedNodeEvents};
use flatdb_core::persistence_manager::PersistenceManager;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{StateId, TreePath};
use rstest::rstest;

/// The base ending at block `block`: a state node and a storage node of its own, each with a placeholder beside it.
fn base(block: u64) -> Arc<Snapshot> {
    let (from, to) = (state(&(block - 1).to_string()), state(&block.to_string()));
    let byte = block as u8;
    snapshot(if block == 1 { BLOCK0 } else { from }, to, |content| {
        content.set_state_node(TreePath::from_hex(&format!("{byte:02x}")), [byte; 32], &[0xC1, byte]);
        content.set_state_node(TreePath::from_hex(&format!("{byte:02x}1")), [byte; 32], &[]);
        content.set_storage_node(
            [byte; 32],
            TreePath::from_hex("a"),
            [byte ^ 0xFF; 32],
            &[0xC2, byte, byte],
        );
        content.set_storage_node([byte; 32], TreePath::from_hex("b"), [byte ^ 0xFF; 32], &[]);
    })
}

/// The nodes of `base` the C# builder marks: every node but the placeholders, in key order.
fn marked_nodes(base: &Snapshot) -> Vec<PersistedNode> {
    let state_nodes = base
        .state_nodes()
        .filter(|(_, node)| !node.is_hash_only_placeholder())
        .map(|(path, node)| PersistedNode {
            address_hash: None,
            path: *path,
            hash: *node.hash,
        });
    let storage_nodes = base
        .storage_nodes()
        .filter(|(_, node)| !node.is_hash_only_placeholder())
        .map(|(key, node)| PersistedNode {
            address_hash: Some(key.address_hash),
            path: key.path,
            hash: *node.hash,
        });
    let mut nodes: Vec<PersistedNode> = state_nodes.chain(storage_nodes).collect();
    nodes.sort_by_key(|node| (node.address_hash, node.path.path, node.path.length, node.hash));
    nodes
}

/// Converting a `CompactSize` range queues one entry per base in ascending block order, listing exactly the nodes the
/// base wrote but its placeholders; the persists from the tier that follow queue entries without nodes after them; a
/// disabled queue keeps nothing.
#[rstest]
fn conversions_queue_the_nodes_of_each_base_in_block_order(#[values(false, true)] enabled: bool) {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG_LF, Some(BLOCK0)).unwrap();
    let events = Arc::new(PersistedNodeEvents::new(enabled));
    let persistence_manager = PersistenceManager::new(
        PM_CONFIG_LF,
        fixture.schedule,
        fixture.host.clone(),
        Arc::clone(&fixture.persistence),
        Arc::clone(&fixture.repository),
        Arc::clone(&events),
        Some(fixture.tier().persisted_tier()),
    );
    let bases: Vec<Arc<Snapshot>> = (1..=16).map(base).collect();
    for base in &bases {
        assert!(fixture.repository.try_add(Arc::clone(base), SnapshotTier::InMemoryBase));
        fixture.repository.add_state_id(base.to());
    }
    let compacted = fixture.add(BLOCK0, state("16"), true);

    persistence_manager.convert_compacted_range(&compacted).unwrap();
    assert_eq!(persistence_manager.flush_to_persistence().unwrap(), state("16"));

    let queued = events.drain(usize::MAX);
    if !enabled {
        assert!(queued.is_empty());
        return;
    }
    let range = |base: &Arc<Snapshot>| (base.from(), base.to());
    let expected: Vec<(EventKind, (StateId, StateId), Vec<PersistedNode>)> = bases
        .iter()
        .map(|base| (EventKind::Converted, range(base), marked_nodes(base)))
        .chain(bases.iter().map(|base| (EventKind::Persisted, range(base), Vec::new())))
        .collect();
    let actual: Vec<(EventKind, (StateId, StateId), Vec<PersistedNode>)> = queued
        .into_iter()
        .map(|event| {
            let mut nodes = event.nodes;
            nodes.sort_by_key(|node| (node.address_hash, node.path.path, node.path.length, node.hash));
            (event.kind, (event.from, event.to), nodes)
        })
        .collect();
    assert_eq!(actual, expected);
}
