// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Decoded, linked nodes the trie warmer loaded for one block.

use crate::buffer::{BufferPointer, BufferSession, NodeRecordBuffer};
use crate::bulk_set::{matched_length, EMPTY_TREE_HASH};
use crate::keccak::keccak;
use crate::node::{
    decode_rlp, nibble, record_length, write_warmed_record, ChildRef, Nibbles, Node, NodeKind,
    RecordPtr, TreePath, TRANSIENT_POINTER,
};
use crate::{Error, NodeStore};
use rustc_hash::FxBuildHasher;
use std::collections::HashMap;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;

const CHUNK_BYTES: usize = 1 << 20;
const INDEX_SHARDS: usize = 64;
static NEXT_EPOCH: AtomicU32 = AtomicU32::new(0);
type Position = ([u8; 32], TreePath);
type Index = HashMap<Position, WarmedRecord, FxBuildHasher>;

/// Records are immutable after publication and their chunks never move. The owner must keep this resource alive
/// and must not reset it while a bulk set or warmer reads its records.
pub struct WarmedNodes {
    arena: Mutex<Arena>,
    index: Box<[Mutex<Index>]>,
}

#[derive(Clone, Copy)]
struct WarmedRecord {
    record: RecordPtr,
    pointer: BufferPointer,
}

struct Arena {
    chunks: Vec<Box<[u8]>>,
    used: usize,
    epoch: u32,
    records: Vec<(Position, RecordPtr)>,
}

fn next_epoch() -> u32 {
    // Never reuse an identity: old prepared references must not alias a new resource after reset or drop.
    NEXT_EPOCH
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |epoch| {
            epoch.checked_add(1).filter(|next| *next < 1 << 31)
        })
        .expect("transient node identities exhausted")
        + 1
}

impl Default for WarmedNodes {
    fn default() -> Self {
        Self {
            arena: Mutex::new(Arena {
                chunks: Vec::new(),
                used: 0,
                epoch: next_epoch(),
                records: Vec::new(),
            }),
            index: (0..INDEX_SHARDS).map(|_| Mutex::default()).collect(),
        }
    }
}

impl WarmedNodes {
    pub fn get(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<RecordPtr> {
        self.get_linked(address, path, hash)
            .map(|record| record.record)
    }

    fn get_linked(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<WarmedRecord> {
        let key = (address.copied().unwrap_or_default(), *path);
        let record = self.shard(&key).lock().unwrap().get(&key).copied()?;
        (record.record.node().hash() == hash).then_some(record)
    }

    /// Resolves a child link independently of the position index. Check identity before touching arena bytes, then
    /// verify the full trie position and hash rather than trusting a stale or foreign resource's pointer.
    pub fn get_pointer(
        &self,
        pointer: BufferPointer,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<RecordPtr> {
        let arena = self.arena.lock().unwrap();
        if pointer >> 32 != (1 << 31 | arena.epoch) as u64 {
            return None;
        }
        let (position, record) = arena.records.get(pointer as u32 as usize)?;
        (*position == (address.copied().unwrap_or_default(), *path) && record.node().hash() == hash)
            .then_some(*record)
    }

    pub fn insert(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
        rlp: &[u8],
    ) -> Result<RecordPtr, Error> {
        let node = decode_rlp(rlp)?;
        Ok(self.insert_linked(address, path, hash, rlp, &node).record)
    }

    fn insert_linked(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
        rlp: &[u8],
        node: &Node,
    ) -> WarmedRecord {
        let key = (address.copied().unwrap_or_default(), *path);
        let mut shard = self.shard(&key).lock().unwrap();
        if let Some(&existing) = shard.get(&key) {
            if existing.record.node().hash() == hash {
                return existing;
            }
        }
        let length = record_length(node, rlp.len());
        let mut arena = self.arena.lock().unwrap();
        let Arena {
            chunks,
            used,
            epoch,
            records,
        } = &mut *arena;
        let chunk = match chunks.last_mut() {
            Some(chunk) if chunk.len() - *used >= length => chunk,
            _ => {
                chunks.push(vec![0; CHUNK_BYTES.max(length)].into_boxed_slice());
                *used = 0;
                chunks.last_mut().unwrap()
            }
        };
        let out = &mut chunk[*used..*used + length];
        write_warmed_record(node, hash, rlp, out);
        *used += length;
        let record = RecordPtr::of(out);
        let position = u32::try_from(records.len()).expect("too many transient node records");
        let pointer = TRANSIENT_POINTER | (*epoch as u64) << 32 | position as u64;
        records.push((key, record));
        let entry = WarmedRecord { record, pointer };
        shard.insert(key, entry);
        entry
    }

    /// Drops every record, retaining the first chunk. Requires exclusive ownership of all previously returned records.
    pub fn reset(&self) {
        for shard in self.index.iter() {
            shard.lock().unwrap().clear();
        }
        let mut arena = self.arena.lock().unwrap();
        arena.epoch = next_epoch();
        arena.records.clear();
        arena.chunks.truncate(1);
        arena.used = 0;
    }

    fn shard(&self, key: &Position) -> &Mutex<Index> {
        &self.index[(key.1.path[0] ^ key.0[0]) as usize % INDEX_SHARDS]
    }
}

struct Frame {
    path: TreePath,
    hash: [u8; 32],
    source: FrameSource,
    child_slot: Option<u8>,
    sibling: Option<(u8, BufferPointer)>,
}

enum FrameSource {
    Record(WarmedRecord),
    Fetched { node: Node, rlp: Vec<u8> },
}

impl Frame {
    fn kind(&self) -> NodeKind {
        match &self.source {
            FrameSource::Record(record) => record.record.node().kind(),
            FrameSource::Fetched { node, .. } => match node {
                Node::Leaf { .. } => NodeKind::Leaf,
                Node::Extension { .. } => NodeKind::Extension,
                Node::Branch { .. } => NodeKind::Branch,
            },
        }
    }
    fn key(&self) -> Nibbles {
        match &self.source {
            FrameSource::Record(record) => record.record.node().key(),
            FrameSource::Fetched {
                node: Node::Leaf { key, .. } | Node::Extension { key, .. },
                ..
            } => *key,
            _ => Nibbles::from_slice(&[]),
        }
    }
    fn child(&self, slot: u8) -> ChildRef {
        match &self.source {
            FrameSource::Record(record) => record.record.node().child(slot),
            FrameSource::Fetched {
                node: Node::Extension { child, .. },
                ..
            } => child.clone(),
            FrameSource::Fetched {
                node: Node::Branch { children },
                ..
            } => children[slot as usize].clone(),
            _ => unreachable!("a leaf has no child"),
        }
    }
    fn publish(
        mut self,
        warmed: &WarmedNodes,
        address: Option<&[u8; 32]>,
        child: BufferPointer,
    ) -> BufferPointer {
        match &mut self.source {
            FrameSource::Record(record) => record.pointer,
            FrameSource::Fetched { node, rlp } => {
                let mut link = |slot: u8, pointer: BufferPointer| {
                    let reference = match node {
                        Node::Extension { child, .. } => child,
                        Node::Branch { children } => &mut children[slot as usize],
                        _ => return,
                    };
                    if let ChildRef::Hash {
                        pointer: target, ..
                    } = reference
                    {
                        *target = pointer;
                    }
                };
                if let Some(slot) = self.child_slot {
                    link(slot, child);
                }
                if let Some((slot, pointer)) = self.sibling {
                    link(slot, pointer);
                }
                warmed
                    .insert_linked(address, &self.path, &self.hash, rlp, node)
                    .pointer
            }
        }
    }
}

fn read_frame<S: NodeStore>(
    store: &S,
    session: &BufferSession<'_>,
    warmed: &WarmedNodes,
    address: Option<&[u8; 32]>,
    path: TreePath,
    hash: [u8; 32],
    pointer: BufferPointer,
) -> Result<Option<Frame>, Error> {
    let record = session
        .record(pointer)
        .map(|record| WarmedRecord {
            record: RecordPtr::of(record),
            pointer,
        })
        .or_else(|| {
            warmed
                .get_pointer(pointer, address, &path, &hash)
                .map(|record| WarmedRecord { record, pointer })
        })
        .or_else(|| warmed.get_linked(address, &path, &hash));
    let source = match record {
        Some(record) => FrameSource::Record(record),
        None => {
            let fetched = store
                .fetch_rlp(&path, &hash, |rlp| {
                    if keccak(rlp) != hash {
                        return None;
                    }
                    decode_rlp(rlp).ok().map(|node| FrameSource::Fetched {
                        node,
                        rlp: rlp.to_vec(),
                    })
                })?
                .flatten();
            let Some(source) = fetched else {
                return Ok(None);
            };
            source
        }
    };
    Ok(Some(Frame {
        path,
        hash,
        source,
        child_slot: None,
        sibling: None,
    }))
}

/// Descends the verified path before publishing fetched records from deepest to root, linking warmed children.
/// A persistence miss or malformed/hash-mismatching node leaves only the verified prefix warmed.
pub fn warm_up<S: NodeStore>(
    store: &S,
    buffer: &NodeRecordBuffer,
    warmed: &WarmedNodes,
    address: Option<&[u8; 32]>,
    root: &[u8; 32],
    key: &[u8; 32],
    for_delete: bool,
) -> Result<(), Error> {
    if *root == EMPTY_TREE_HASH {
        return Ok(());
    }
    let session = buffer.pin();
    let mut frames = Vec::with_capacity(65);
    let mut path = TreePath::default();
    let mut child = ChildRef::Hash {
        hash: *root,
        pointer: session.find_root(address, root),
    };
    let mut parent_branch = None;
    loop {
        match child {
            ChildRef::Hash { hash, pointer } => {
                let Some(mut frame) =
                    read_frame(store, &session, warmed, address, path, hash, pointer)?
                else {
                    break;
                };
                match frame.kind() {
                    NodeKind::Leaf => {
                        let leaf_key = frame.key();
                        frames.push(frame);
                        if for_delete && matches_leaf(leaf_key, &path, key) {
                            warm_sibling(
                                store,
                                &session,
                                warmed,
                                address,
                                &mut frames,
                                parent_branch,
                            )?;
                        }
                        break;
                    }
                    NodeKind::Extension => {
                        let extension = frame.key();
                        if extension.len() > 64 - path.depth()
                            || matched_length(extension.as_slice(), key, path.depth())
                                != extension.len()
                        {
                            frames.push(frame);
                            break;
                        }
                        frame.child_slot = Some(0);
                        child = frame.child(0);
                        path.push_all(extension.as_slice());
                        parent_branch = None;
                    }
                    NodeKind::Branch => {
                        if path.depth() == 64 {
                            frames.push(frame);
                            break;
                        }
                        let slot = nibble(key, path.depth());
                        frame.child_slot = Some(slot);
                        child = frame.child(slot);
                        path.push(slot);
                        parent_branch = Some(frames.len());
                    }
                }
                frames.push(frame);
            }
            ChildRef::Inline(rlp) => {
                if for_delete {
                    if let Node::Leaf { key: leaf_key, .. } = decode_rlp(rlp.as_slice())? {
                        if matches_leaf(leaf_key, &path, key) {
                            warm_sibling(
                                store,
                                &session,
                                warmed,
                                address,
                                &mut frames,
                                parent_branch,
                            )?;
                        }
                    }
                }
                break;
            }
            _ => break,
        }
    }
    let mut child_pointer = 0;
    for frame in frames.into_iter().rev() {
        child_pointer = frame.publish(warmed, address, child_pointer);
    }
    Ok(())
}

fn matches_leaf(leaf_key: Nibbles, path: &TreePath, key: &[u8; 32]) -> bool {
    leaf_key.len() == 64 - path.depth()
        && matched_length(leaf_key.as_slice(), key, path.depth()) == leaf_key.len()
}

fn warm_sibling<S: NodeStore>(
    store: &S,
    session: &BufferSession<'_>,
    warmed: &WarmedNodes,
    address: Option<&[u8; 32]>,
    frames: &mut [Frame],
    parent_branch: Option<usize>,
) -> Result<(), Error> {
    let Some(branch_index) = parent_branch else {
        return Ok(());
    };
    let branch = &mut frames[branch_index];
    let mut sibling = None;
    for slot in 0..16 {
        if Some(slot) == branch.child_slot {
            continue;
        }
        let child = branch.child(slot);
        if matches!(child, ChildRef::Empty) {
            continue;
        }
        if sibling.is_some() {
            return Ok(());
        }
        sibling = Some((slot, child));
    }
    if let Some((slot, ChildRef::Hash { hash, pointer })) = sibling {
        let path = branch.path.append(&[slot]);
        if let Some(frame) = read_frame(store, session, warmed, address, path, hash, pointer)? {
            branch.sibling = Some((slot, frame.publish(warmed, address, 0)));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;
    use crate::keccak::keccak;
    use crate::node::{empty_children, encode, Nibbles, Node};

    /// Holds nodes by path and hash, counting fetches.
    #[derive(Default)]
    struct MapStore {
        nodes: HashMap<(TreePath, [u8; 32]), Vec<u8>>,
        fetches: AtomicUsize,
    }

    impl NodeStore for MapStore {
        fn fetch_rlp<R>(
            &self,
            path: &TreePath,
            hash: &[u8; 32],
            receive: impl FnOnce(&[u8]) -> R,
        ) -> Result<Option<R>, Error> {
            self.fetches.fetch_add(1, Ordering::Relaxed);
            Ok(self.nodes.get(&(*path, *hash)).map(|rlp| receive(rlp)))
        }
    }

    fn rlp(node: &Node) -> Vec<u8> {
        let mut out = Vec::new();
        encode(node, &mut out);
        out
    }

    fn apply_items(
        store: &MapStore,
        pending: &crate::PendingNodes,
        buffer: &NodeRecordBuffer,
        warmed: Option<&WarmedNodes>,
        root: &[u8; 32],
        items: &[([u8; 32], Vec<u8>)],
    ) -> [u8; 32] {
        let mut values = Vec::new();
        let mut entries: Vec<_> = items
            .iter()
            .map(|(key, value)| {
                let entry = crate::Entry {
                    key: *key,
                    offset: values.len() as i32,
                    length: value.len() as i32,
                };
                values.extend_from_slice(value);
                entry
            })
            .collect();
        crate::bulk_set(
            store,
            pending,
            Some(buffer),
            warmed,
            None,
            root,
            &mut entries,
            &values,
        )
        .unwrap()
    }

    fn persisted_items(items: &[([u8; 32], Vec<u8>)]) -> (MapStore, [u8; 32]) {
        let buffer = NodeRecordBuffer::minimal();
        let pending = crate::PendingNodes::default();
        let mut store = MapStore::default();
        let root = apply_items(&store, &pending, &buffer, None, &EMPTY_TREE_HASH, items);
        for shard in 0..pending.shard_count() {
            pending.with_view(shard, |nodes, rlp| {
                for node in nodes {
                    store.nodes.insert(
                        (node.path, node.hash),
                        rlp[node.rlp_offset as usize..][..node.rlp_length as usize].to_vec(),
                    );
                }
            });
        }
        (store, root)
    }

    fn pair() -> Vec<([u8; 32], Vec<u8>)> {
        let mut first = [0; 32];
        first[0] = 0x10;
        let mut second = first;
        second[0] = 0x20;
        vec![(first, vec![1; 40]), (second, vec![2; 40])]
    }

    #[test]
    fn transient_pointers_validate_identity_position_and_hash() {
        let warmed = WarmedNodes::default();
        let foreign = WarmedNodes::default();
        let path = TreePath::default().append(&[1]);
        let address = [3; 32];
        let node = Node::Leaf {
            key: Nibbles::from_slice(&[0; 63]),
            value: vec![1; 40],
        };
        let bytes = rlp(&node);
        let hash = keccak(&bytes);
        let first = warmed.insert_linked(Some(&address), &path, &hash, &bytes, &node);
        let other = foreign.insert_linked(Some(&address), &path, &hash, &bytes, &node);
        assert_ne!(first.pointer, other.pointer);
        assert!(warmed
            .get_pointer(first.pointer, Some(&address), &path, &hash)
            .is_some());
        assert!(warmed
            .get_pointer(first.pointer, None, &path, &hash)
            .is_none());
        assert!(warmed
            .get_pointer(first.pointer, Some(&address), &TreePath::default(), &hash)
            .is_none());
        assert!(warmed
            .get_pointer(first.pointer, Some(&address), &path, &[4; 32])
            .is_none());
        assert!(foreign
            .get_pointer(first.pointer, Some(&address), &path, &hash)
            .is_none());
        warmed.reset();
        let next = warmed.insert_linked(Some(&address), &path, &hash, &bytes, &node);
        assert_ne!(first.pointer, next.pointer);
        assert!(warmed
            .get_pointer(first.pointer, Some(&address), &path, &hash)
            .is_none());
    }

    #[test]
    fn warmed_children_survive_position_index_misses_and_commit_without_transient_links() {
        let items = pair();
        let (store, root) = persisted_items(&items);
        let buffer = NodeRecordBuffer::minimal();
        let warmed = WarmedNodes::default();
        warm_up(&store, &buffer, &warmed, None, &root, &items[0].0, true).unwrap();
        let branch = warmed
            .get(None, &TreePath::default(), &root)
            .unwrap()
            .node();
        for slot in [1, 2] {
            let ChildRef::Hash { hash, pointer } = branch.child(slot) else {
                panic!("hashed child");
            };
            assert_ne!(pointer & TRANSIENT_POINTER, 0);
            let path = TreePath::default().append(&[slot]);
            warmed
                .shard(&([0; 32], path))
                .lock()
                .unwrap()
                .remove(&([0; 32], path));
            assert!(warmed.get(None, &path, &hash).is_none());
            assert!(warmed.get_pointer(pointer, None, &path, &hash).is_some());
        }
        let before = store.fetches.load(Ordering::Relaxed);
        warm_up(&store, &buffer, &warmed, None, &root, &items[0].0, true).unwrap();
        assert_eq!(store.fetches.load(Ordering::Relaxed), before);
        let changed = [(items[0].0, vec![3; 40])];
        let updated = apply_items(
            &store,
            &crate::PendingNodes::default(),
            &buffer,
            Some(&warmed),
            &root,
            &changed,
        );
        assert_eq!(store.fetches.load(Ordering::Relaxed), before);
        let session = buffer.pin();
        let node = crate::node::BufferedNode::new(
            session.record(session.find_root(None, &updated)).unwrap(),
        );
        let ChildRef::Hash {
            pointer: changed_pointer,
            ..
        } = node.child(1)
        else {
            panic!("hashed changed child");
        };
        let ChildRef::Hash {
            pointer: inherited_pointer,
            hash,
        } = node.child(2)
        else {
            panic!("hashed inherited child");
        };
        assert_ne!(changed_pointer, 0);
        assert_eq!(changed_pointer & TRANSIENT_POINTER, 0);
        assert_eq!(inherited_pointer, 0);
        assert_eq!(
            hash,
            match branch.child(2) {
                ChildRef::Hash { hash, .. } => hash,
                _ => unreachable!(),
            }
        );
        drop(session);
        warmed.reset();
        let final_root = apply_items(
            &store,
            &crate::PendingNodes::default(),
            &buffer,
            Some(&warmed),
            &updated,
            &[(items[1].0, vec![4; 40])],
        );
        let (_, expected) =
            persisted_items(&[(items[0].0, vec![3; 40]), (items[1].0, vec![4; 40])]);
        assert_eq!(final_root, expected);
    }

    #[test]
    fn warming_publishes_only_the_verified_prefix_after_descent() {
        struct ObservedStore<'a> {
            store: MapStore,
            warmed: &'a WarmedNodes,
            root: [u8; 32],
        }
        impl NodeStore for ObservedStore<'_> {
            fn fetch_rlp<R>(
                &self,
                path: &TreePath,
                hash: &[u8; 32],
                receive: impl FnOnce(&[u8]) -> R,
            ) -> Result<Option<R>, Error> {
                assert!(
                    self.warmed
                        .get(None, &TreePath::default(), &self.root)
                        .is_none(),
                    "root published before descent completed"
                );
                self.store.fetch_rlp(path, hash, receive)
            }
        }
        for invalid in 0..3 {
            let warmed = WarmedNodes::default();
            let bytes = if invalid == 2 {
                vec![0xc0]
            } else {
                rlp(&Node::Leaf {
                    key: Nibbles::from_slice(&[0; 63]),
                    value: vec![1; 40],
                })
            };
            let hash = keccak(&bytes);
            let node = Node::Extension {
                key: Nibbles::from_slice(&[1]),
                child: ChildRef::Hash { hash, pointer: 0 },
            };
            let root_rlp = rlp(&node);
            let root = keccak(&root_rlp);
            let path = TreePath::default().append(&[1]);
            let mut source = MapStore::default();
            source.nodes.insert((TreePath::default(), root), root_rlp);
            if invalid != 0 {
                source
                    .nodes
                    .insert((path, hash), if invalid == 1 { vec![0xc0] } else { bytes });
            }
            let store = ObservedStore {
                store: source,
                warmed: &warmed,
                root,
            };
            let mut key = [0; 32];
            key[0] = 0x10;
            warm_up(
                &store,
                &NodeRecordBuffer::minimal(),
                &warmed,
                None,
                &root,
                &key,
                false,
            )
            .unwrap();
            let parent = warmed
                .get(None, &TreePath::default(), &root)
                .unwrap()
                .node();
            assert!(matches!(parent.child(0), ChildRef::Hash { pointer: 0, .. }));
            assert!(warmed.get(None, &path, &hash).is_none());
        }
    }

    #[test]
    fn no_op_warmed_roots_never_enter_the_buffer_root_index() {
        let items = pair();
        let (store, root) = persisted_items(&items);
        let buffer = NodeRecordBuffer::minimal();
        let warmed = WarmedNodes::default();
        warm_up(&store, &buffer, &warmed, None, &root, &items[0].0, true).unwrap();
        let pending = crate::PendingNodes::default();
        assert_eq!(
            apply_items(&store, &pending, &buffer, Some(&warmed), &root, &items),
            root
        );
        assert_eq!(pending.count(), 0);
        assert_eq!(buffer.pin().find_root(None, &root), 0);
        warmed.reset();
        assert_eq!(
            apply_items(&store, &pending, &buffer, Some(&warmed), &root, &items),
            root
        );
    }

    #[test]
    fn prepared_updates_resolve_reset_or_foreign_transient_children_from_persistence() {
        for replace_resource in [false, true] {
            let items = pair();
            let (store, root) = persisted_items(&items);
            let buffer = NodeRecordBuffer::minimal();
            let warmed = WarmedNodes::default();
            warm_up(&store, &buffer, &warmed, None, &root, &items[0].0, true).unwrap();
            let mut state = crate::bulk_set::BulkSetState::new(root, None);
            let mut entries = [crate::Entry {
                key: items[0].0,
                offset: 0,
                length: 40,
            }];
            state
                .update(&store, &buffer, Some(&warmed), &mut entries, &[3; 40])
                .unwrap();
            let foreign = WarmedNodes::default();
            let next = if replace_resource {
                &foreign
            } else {
                warmed.reset();
                &warmed
            };
            let before = store.fetches.load(Ordering::Relaxed);
            let mut corrections = [crate::Entry {
                key: items[1].0,
                offset: 0,
                length: 40,
            }];
            let final_root = state
                .finish(
                    &store,
                    &crate::PendingNodes::default(),
                    &buffer,
                    Some(next),
                    &mut corrections,
                    &[4; 40],
                )
                .unwrap();
            assert!(store.fetches.load(Ordering::Relaxed) > before);
            let (_, expected) =
                persisted_items(&[(items[0].0, vec![3; 40]), (items[1].0, vec![4; 40])]);
            assert_eq!(final_root, expected);
        }
    }

    #[test]
    fn delete_warming_fetches_only_the_sibling_needed_for_branch_collapse() {
        let mut key_a = [0; 32];
        key_a[0] = 0x10;
        key_a[31] = 1;
        let mut key_b = key_a;
        key_b[31] = 2;
        let mut key_c = [0; 32];
        key_c[0] = 0x20;
        let mut key_d = key_a;
        key_d[31] = 3;
        for for_delete in [false, true] {
            for three_children in [false, true] {
                for inline in [false, true] {
                    let mut items = vec![
                        (key_a, if inline { vec![1] } else { vec![1; 40] }),
                        (key_b, vec![2; 40]),
                        (key_c, vec![3; 40]),
                    ];
                    if three_children {
                        items.push((key_d, vec![4; 40]));
                    }
                    let (store, root) = persisted_items(&items);
                    let buffer = NodeRecordBuffer::minimal();
                    let warmed = WarmedNodes::default();
                    warm_up(&store, &buffer, &warmed, None, &root, &key_a, for_delete).unwrap();
                    let before = store.fetches.load(Ordering::Relaxed);
                    assert_eq!(
                        before,
                        3 + usize::from(!inline) + usize::from(for_delete && !three_children)
                    );
                    let updated = apply_items(
                        &store,
                        &crate::PendingNodes::default(),
                        &buffer,
                        Some(&warmed),
                        &root,
                        &[(key_a, Vec::new())],
                    );
                    assert_eq!(
                        store.fetches.load(Ordering::Relaxed) - before,
                        usize::from(!for_delete && !three_children)
                    );
                    items.retain(|(key, _)| *key != key_a);
                    let (_, expected) = persisted_items(&items);
                    assert_eq!(updated, expected);
                }
            }
        }
    }

    #[test]
    fn delete_warming_of_a_missing_key_does_not_load_an_off_path_sibling() {
        let mut key_a = [0; 32];
        key_a[0] = 0x10;
        key_a[31] = 1;
        let mut key_b = [0; 32];
        key_b[0] = 0x20;
        key_b[31] = 2;
        for diverging_leaf in [false, true] {
            let (store, root) = persisted_items(&[(key_a, vec![1; 40]), (key_b, vec![2; 40])]);
            let mut missing = key_a;
            missing[0] = if diverging_leaf { 0x10 } else { 0x30 };
            missing[31] = 4;
            let buffer = NodeRecordBuffer::minimal();
            warm_up(
                &store,
                &buffer,
                &WarmedNodes::default(),
                None,
                &root,
                &missing,
                false,
            )
            .unwrap();
            let ordinary = store.fetches.load(Ordering::Relaxed);
            warm_up(
                &store,
                &buffer,
                &WarmedNodes::default(),
                None,
                &root,
                &missing,
                true,
            )
            .unwrap();
            assert_eq!(store.fetches.load(Ordering::Relaxed), 2 * ordinary);
        }
    }

    #[test]
    fn warm_up_fetches_only_nodes_neither_buffered_nor_warmed() {
        // A buffered root branch whose child 1 is buffered, and whose child 2, an extension over a leaf, is only in the store.
        let buffer = NodeRecordBuffer::minimal();
        let session = buffer.pin();
        let leaf = Node::Leaf {
            key: Nibbles::from_slice(&[0; 61]),
            value: vec![0xaa; 40],
        };
        let buffered_leaf = session.append(&leaf);
        let stored_leaf_hash = keccak(&rlp(&leaf));
        let extension = Node::Extension {
            key: Nibbles::from_slice(&[0, 0]),
            child: ChildRef::Hash {
                hash: stored_leaf_hash,
                pointer: 0,
            },
        };
        let mut children = empty_children();
        children[1] = ChildRef::Hash {
            hash: [0x22; 32],
            pointer: buffered_leaf,
        };
        children[2] = ChildRef::Hash {
            hash: keccak(&rlp(&extension)),
            pointer: 0,
        };
        let root = [0x44; 32];
        let address = [0x55; 32];
        session.set_root(
            Some(&address),
            &root,
            session.append(&Node::Branch { children }),
        );
        drop(session);

        let path = |nibbles: &[u8]| TreePath::default().append(nibbles);
        let mut store = MapStore::default();
        store
            .nodes
            .insert((path(&[2]), keccak(&rlp(&extension))), rlp(&extension));
        store
            .nodes
            .insert((path(&[2, 0, 0]), stored_leaf_hash), rlp(&leaf));
        let warmed = WarmedNodes::default();
        let key = |first: u8| {
            let mut key = [0; 32];
            key[0] = first;
            key
        };
        let warm = |address: Option<&[u8; 32]>, root: &[u8; 32], key: [u8; 32]| {
            let before = store.fetches.load(Ordering::Relaxed);
            warm_up(&store, &buffer, &warmed, address, root, &key, false).unwrap();
            store.fetches.load(Ordering::Relaxed) - before
        };

        // The buffered part of a path is never fetched, the rest once, and an unknown root ends the walk at once.
        assert_eq!(warm(Some(&address), &root, key(0x10)), 0);
        assert_eq!(warm(Some(&address), &root, key(0x20)), 2);
        assert_eq!(warm(Some(&address), &root, key(0x20)), 0);
        assert_eq!(warm(Some(&address), &[0x66; 32], key(0x10)), 1);
        assert_eq!(warm(None, &root, key(0x10)), 1);
        assert_eq!(warm(Some(&address), &EMPTY_TREE_HASH, key(0x10)), 0);

        // Warmed records hold the node, its hash and RLP, under their trie, path and hash only.
        let warmed_leaf = warmed
            .get(Some(&address), &path(&[2, 0, 0]), &stored_leaf_hash)
            .unwrap()
            .node();
        assert_eq!(warmed_leaf.rlp(), rlp(&leaf));
        assert_eq!(rlp(&warmed_leaf.to_owned(0)), rlp(&leaf));
        assert!(warmed
            .get(None, &path(&[2, 0, 0]), &stored_leaf_hash)
            .is_none());
        assert!(warmed
            .get(Some(&address), &path(&[2, 0, 0]), &[0x77; 32])
            .is_none());
        warmed.reset();
        assert!(warmed
            .get(Some(&address), &path(&[2, 0, 0]), &stored_leaf_hash)
            .is_none());
    }
}
