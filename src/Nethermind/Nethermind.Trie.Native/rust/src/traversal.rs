// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Root-based snapshot reads, with traversal restricted to an immutable allocation interval.

use crate::log::{LogPointer, LogSession, TrieNodeLog};
use crate::node::{nibble, ChildRef, LoggedNode, NodeKind, TreePath};

impl LogSession<'_> {
    /// Reads the hash-referenced record exactly at `path`, including inherited compacted records when available.
    pub(crate) fn find_node(&self, mut pointer: LogPointer, path: &TreePath) -> Option<LoggedNode<'_>> {
        let mut depth = 0;
        loop {
            let node = LoggedNode::new(self.record(pointer)?);
            if depth == path.depth() {
                return Some(node);
            }
            let child = match node.kind() {
                NodeKind::Leaf => return None,
                NodeKind::Extension => {
                    let key = node.key();
                    if depth + key.len() > path.depth()
                        || key.as_slice().iter().enumerate().any(|(i, &value)| nibble(&path.path, depth + i) != value)
                    {
                        return None;
                    }
                    depth += key.len();
                    node.child(0)
                }
                NodeKind::Branch => {
                    let child = node.child(nibble(&path.path, depth));
                    depth += 1;
                    child
                }
            };
            pointer = match child {
                ChildRef::Hash { pointer, .. } => pointer,
                _ => return None,
            };
        }
    }
}

/// Traverses only writes at or after `boundary`; the retained generation protects every dereferenced pointer.
/// Children precede their immutable parent in allocation order, so an older parent skips its whole subtree.
/// The caller must keep the boundary's original retain alive while constructing this additional retain.
pub(crate) struct SnapshotCursor<'a> {
    log: &'a TrieNodeLog,
    boundary: LogPointer,
    stack: Vec<(TreePath, LogPointer)>,
}

impl<'a> SnapshotCursor<'a> {
    pub(crate) fn new(log: &'a TrieNodeLog, root: LogPointer, boundary: LogPointer) -> Self {
        log.retain(boundary >> 32);
        let mut stack = Vec::new();
        if root != 0 && root >= boundary {
            stack.push((TreePath::default(), root));
        }
        Self { log, boundary, stack }
    }
}

impl Iterator for SnapshotCursor<'_> {
    type Item = (TreePath, LogPointer);

    fn next(&mut self) -> Option<Self::Item> {
        let (path, pointer) = self.stack.pop()?;
        let node = self.log.node(pointer);
        match node.kind() {
            NodeKind::Leaf => {}
            NodeKind::Extension => {
                if node.hash_mask() != 0 {
                    let child = node.pointer(0);
                    if child != 0 && child >= self.boundary {
                        self.stack.push((path.append(node.key().as_slice()), child));
                    }
                }
            }
            NodeKind::Branch => {
                let mut mask = node.hash_mask();
                let mut position = mask.count_ones() as usize;
                while mask != 0 {
                    let index = 15 - mask.leading_zeros() as u8;
                    mask &= !(1 << index);
                    position -= 1;
                    let child = node.pointer(position);
                    if child != 0 && child >= self.boundary {
                        self.stack.push((path.append(&[index]), child));
                    }
                }
            }
        }
        Some((path, pointer))
    }
}

impl Drop for SnapshotCursor<'_> {
    fn drop(&mut self) {
        self.log.release(self.boundary >> 32);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::keccak::keccak;
    use crate::node::{empty_children, encode, InlineRlp, Nibbles, Node};

    fn leaf(value: u8) -> Node {
        Node::Leaf { key: Nibbles::from_slice(&[0; 63]), value: vec![value; 40] }
    }

    fn reference(log: &TrieNodeLog, pointer: LogPointer) -> ChildRef {
        ChildRef::Hash { hash: *log.node(pointer).hash(), pointer }
    }

    #[test]
    fn same_generation_boundary_skips_inherited_and_unreachable_nodes() {
        let log = TrieNodeLog::minimal();
        let (generation, first_boundary) = log.retain_active_boundary();
        let session = log.pin();
        let inherited = session.append(&leaf(1));
        let (same_generation, boundary) = log.retain_active_boundary();
        assert_eq!(same_generation, generation);
        assert!(boundary > first_boundary);
        let unrelated = session.append(&leaf(2));
        let changed = session.append(&leaf(3));
        let mut children = empty_children();
        children[1] = reference(&log, inherited);
        children[2] = reference(&log, changed);
        let root = session.append(&Node::Branch { children });
        let nodes: Vec<_> = SnapshotCursor::new(&log, root, boundary).collect();
        assert_eq!(nodes, vec![(TreePath::default(), root), (TreePath::default().append(&[2]), changed)]);
        assert!(!nodes.iter().any(|(_, pointer)| *pointer == inherited || *pointer == unrelated));
        assert_eq!(session.find_node(root, &TreePath::default().append(&[1])).unwrap().rlp(), log.node(inherited).rlp());
        assert!(session.find_node(root, &TreePath::default().append(&[3])).is_none());
        assert!(SnapshotCursor::new(&log, inherited, boundary).next().is_none());
        log.release(generation);
        log.release(same_generation);
    }

    #[test]
    fn extension_paths_are_exact_and_inline_children_are_not_emitted() {
        let log = TrieNodeLog::minimal();
        let (generation, boundary) = log.retain_active_boundary();
        let session = log.pin();
        let child = session.append(&leaf(1));
        let mut children = empty_children();
        children[3] = reference(&log, child);
        let mut inline = Vec::new();
        encode(&Node::Leaf { key: Nibbles::from_slice(&[1]), value: vec![1] }, &mut inline);
        // Decode the inline child through its parent, just as a stored record does.
        children[4] = ChildRef::Inline(InlineRlp::new(&inline));
        let branch = session.append(&Node::Branch { children });
        let root = session.append(&Node::Extension { key: Nibbles::from_slice(&[1, 2]), child: reference(&log, branch) });
        let nodes: Vec<_> = SnapshotCursor::new(&log, root, boundary).collect();
        assert_eq!(nodes.iter().map(|(path, _)| *path).collect::<Vec<_>>(), vec![TreePath::default(), TreePath::default().append(&[1,2]), TreePath::default().append(&[1,2,3])]);
        assert_eq!(session.find_node(root, &TreePath::default().append(&[1,2,3])).unwrap().hash(), &keccak(log.node(child).rlp()));
        assert!(session.find_node(root, &TreePath::default().append(&[1])).is_none());
        assert!(session.find_node(root, &TreePath::default().append(&[1,3])).is_none());
        assert!(session.find_node(root, &TreePath::default().append(&[1,2,4])).is_none());
        log.release(generation);
    }
}
