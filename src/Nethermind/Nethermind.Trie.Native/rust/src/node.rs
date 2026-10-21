// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Node model and its standard Ethereum node RLP, whose keccak is the node hash.

use crate::buffer::BufferPointer;
use crate::Error;

const EMPTY_STRING: u8 = 0x80;
const HASH_PREFIX: u8 = 0xa0;

/// Same layout as Nethermind's `TreePath`: nibbles packed high nibble first, bytes past `length` are zero.
#[repr(C)]
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq, Hash)]
pub struct TreePath {
    pub path: [u8; 32],
    pub length: i32,
    padding: i32,
}

impl TreePath {
    pub fn depth(&self) -> usize {
        self.length as usize
    }

    pub fn push(&mut self, value: u8) {
        let index = self.length as usize;
        if index % 2 == 0 {
            self.path[index / 2] = value << 4;
        } else {
            self.path[index / 2] |= value;
        }
        self.length += 1;
    }

    pub fn push_all(&mut self, nibbles: &[u8]) {
        for &value in nibbles {
            self.push(value);
        }
    }

    pub fn set_last(&mut self, value: u8) {
        let index = self.length as usize - 1;
        if index % 2 == 0 {
            self.path[index / 2] = value << 4;
        } else {
            self.path[index / 2] = (self.path[index / 2] & 0xf0) | value;
        }
    }

    /// Shortens the path to `length` nibbles, zeroing the dropped ones to keep it canonical.
    pub fn truncate(&mut self, length: usize) {
        for index in length..self.length as usize {
            if index % 2 == 0 {
                self.path[index / 2] = 0;
            } else {
                self.path[index / 2] &= 0xf0;
            }
        }
        self.length = length as i32;
    }

    pub fn append(&self, nibbles: &[u8]) -> TreePath {
        let mut result = *self;
        result.push_all(nibbles);
        result
    }
}

pub fn nibble(key: &[u8; 32], index: usize) -> u8 {
    let byte = key[index / 2];
    if index % 2 == 0 {
        byte >> 4
    } else {
        byte & 0x0f
    }
}

#[derive(Clone, Copy)]
pub struct Nibbles {
    length: u8,
    data: [u8; 64],
}

impl Nibbles {
    pub fn from_slice(nibbles: &[u8]) -> Nibbles {
        Nibbles::concat(nibbles, &[])
    }

    pub fn from_key(key: &[u8; 32], from: usize, length: usize) -> Nibbles {
        let mut result = Nibbles {
            length: length as u8,
            data: [0; 64],
        };
        for i in 0..length {
            result.data[i] = nibble(key, from + i);
        }
        result
    }

    pub fn concat(prefix: &[u8], suffix: &[u8]) -> Nibbles {
        let mut result = Nibbles {
            length: (prefix.len() + suffix.len()) as u8,
            data: [0; 64],
        };
        result.data[..prefix.len()].copy_from_slice(prefix);
        result.data[prefix.len()..prefix.len() + suffix.len()].copy_from_slice(suffix);
        result
    }

    pub fn as_slice(&self) -> &[u8] {
        &self.data[..self.length as usize]
    }

    pub fn len(&self) -> usize {
        self.length as usize
    }
}

/// RLP of a node shorter than 32 bytes, embedded in its parent instead of being stored.
#[derive(Clone, Copy)]
pub struct InlineRlp {
    length: u8,
    data: [u8; 31],
}

impl InlineRlp {
    pub(crate) fn new(rlp: &[u8]) -> InlineRlp {
        let mut result = InlineRlp {
            length: rlp.len() as u8,
            data: [0; 31],
        };
        result.data[..rlp.len()].copy_from_slice(rlp);
        result
    }

    pub fn as_slice(&self) -> &[u8] {
        &self.data[..self.length as usize]
    }
}

#[derive(Clone, Default)]
pub enum ChildRef {
    #[default]
    Empty,
    /// A node referenced by `hash`, also at `pointer` in the buffer unless that is zero.
    Hash {
        hash: [u8; 32],
        pointer: BufferPointer,
    },
    Inline(InlineRlp),
    /// A node created during this bulk set that is not encoded yet.
    Pending(Box<Node>),
}

impl ChildRef {
    pub fn inline(rlp: &[u8]) -> ChildRef {
        ChildRef::Inline(InlineRlp::new(rlp))
    }

    fn encoded_length(&self) -> usize {
        match self {
            ChildRef::Empty => 1,
            ChildRef::Hash { .. } => 33,
            ChildRef::Inline(rlp) => rlp.as_slice().len(),
            ChildRef::Pending(_) => unreachable!("pending child must be committed before encoding"),
        }
    }

    fn write(&self, out: &mut Vec<u8>) {
        match self {
            ChildRef::Empty => out.push(EMPTY_STRING),
            ChildRef::Hash { hash, .. } => {
                out.push(HASH_PREFIX);
                out.extend_from_slice(hash);
            }
            ChildRef::Inline(rlp) => out.extend_from_slice(rlp.as_slice()),
            ChildRef::Pending(_) => unreachable!("pending child must be committed before encoding"),
        }
    }
}

#[derive(Clone)]
pub enum Node {
    Leaf { key: Nibbles, value: Vec<u8> },
    Extension { key: Nibbles, child: ChildRef },
    Branch { children: Box<[ChildRef; 16]> },
}

impl Node {
    /// Sets branch child `slot`, or the child of an extension.
    pub fn set_child(&mut self, slot: usize, reference: ChildRef) {
        match self {
            Node::Branch { children } => children[slot] = reference,
            Node::Extension { child, .. } => *child = reference,
            Node::Leaf { .. } => unreachable!("a leaf has no children"),
        }
    }
}

pub fn empty_children() -> Box<[ChildRef; 16]> {
    Box::new([const { ChildRef::Empty }; 16])
}

const LEAF: u8 = 0;
const EXTENSION: u8 = 1;
/// A branch whose every non-empty child is referenced by hash, so each child is found without parsing the others.
const HASHED_BRANCH: u8 = 2;
/// A branch with an inline child, whose children are found by parsing those before them.
const INLINE_BRANCH: u8 = 3;
/// kind: u8, zero: u8, hash mask: u16, RLP length: u32, node hash: [u8; 32], key: u64, see [`crate::buffer::node_key`].
pub const RECORD_HEADER_LENGTH: usize = 48;
const POINTER_LENGTH: usize = 8;
/// High-bit tagged pointers are resource-local warmed records, never persistent buffer addresses.
pub const TRANSIENT_POINTER: u64 = 1 << 63;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum NodeKind {
    Leaf,
    Extension,
    Branch,
}

fn children(node: &Node) -> &[ChildRef] {
    match node {
        Node::Leaf { .. } => &[],
        Node::Extension { child, .. } => std::slice::from_ref(child),
        Node::Branch { children } => &children[..],
    }
}

/// Bit `i` set when child `i` is referenced by hash, the child of an extension being child 0.
fn hash_mask(node: &Node) -> u16 {
    children(node)
        .iter()
        .enumerate()
        .filter(|(_, child)| matches!(child, ChildRef::Hash { .. }))
        .fold(0, |mask, (index, _)| mask | 1 << index)
}

/// Length of the buffer record of `node`, whose RLP is `rlp_length` bytes.
pub fn record_length(node: &Node, rlp_length: usize) -> usize {
    RECORD_HEADER_LENGTH + POINTER_LENGTH * hash_mask(node).count_ones() as usize + rlp_length
}

/// Length of the buffer record starting with `header`.
pub fn record_length_of(header: &[u8]) -> usize {
    RECORD_HEADER_LENGTH
        + POINTER_LENGTH
            * u16::from_le_bytes(header[2..4].try_into().unwrap()).count_ones() as usize
        + u32::from_le_bytes(header[4..8].try_into().unwrap()) as usize
}

/// Writes the buffer record of `node`, whose children must all be resolved, with key `key`, hash `hash` and RLP `rlp`, to
/// `out` of [`record_length`] bytes.
///
/// A record is its header, the buffer pointer of every child referenced by hash, in child order, then the RLP, which
/// [`BufferedNode`] decodes in place.
pub fn write_record(node: &Node, key: u64, hash: &[u8; 32], rlp: &[u8], out: &mut [u8]) {
    write_record_inner(node, key, hash, rlp, out, false);
}

/// Writes a block-owned record, preserving its resource-local child links.
pub fn write_warmed_record(node: &Node, hash: &[u8; 32], rlp: &[u8], out: &mut [u8]) {
    write_record_inner(node, 0, hash, rlp, out, true);
}

fn write_record_inner(node: &Node, key: u64, hash: &[u8; 32], rlp: &[u8], out: &mut [u8], transient: bool) {
    let mask = hash_mask(node);
    out[0] = match node {
        Node::Leaf { .. } => LEAF,
        Node::Extension { .. } => EXTENSION,
        Node::Branch { children }
            if children
                .iter()
                .any(|child| matches!(child, ChildRef::Inline(_))) =>
        {
            INLINE_BRANCH
        }
        Node::Branch { .. } => HASHED_BRANCH,
    };
    out[1] = 0;
    out[2..4].copy_from_slice(&mask.to_le_bytes());
    out[4..8].copy_from_slice(&(rlp.len() as u32).to_le_bytes());
    out[8..40].copy_from_slice(hash);
    out[40..RECORD_HEADER_LENGTH].copy_from_slice(&key.to_le_bytes());
    let mut offset = RECORD_HEADER_LENGTH;
    for child in children(node) {
        match child {
            ChildRef::Hash { pointer, .. } => {
                // A buffered node can outlive or be compacted after the warmed resource is reset.
                let pointer = if !transient && pointer & TRANSIENT_POINTER != 0 { 0 } else { *pointer };
                out[offset..offset + POINTER_LENGTH].copy_from_slice(&pointer.to_le_bytes());
                offset += POINTER_LENGTH;
            }
            ChildRef::Pending(_) => unreachable!("only resolved children are buffered"),
            ChildRef::Empty | ChildRef::Inline(_) => {}
        }
    }
    out[offset..].copy_from_slice(rlp);
}

/// The address of a node record, in a buffer or in warmed nodes, which its holder keeps for as long as it is used.
#[derive(Clone, Copy)]
pub struct RecordPtr(*const u8);

// Records are never written once appended, and a bulk set only reads them while their buffer session or warmed nodes
// keep them.
unsafe impl Send for RecordPtr {}
unsafe impl Sync for RecordPtr {}

impl RecordPtr {
    pub fn of(record: &[u8]) -> RecordPtr {
        RecordPtr(record.as_ptr())
    }

    /// The node of the record, which must still be kept.
    pub fn node<'a>(self) -> BufferedNode<'a> {
        unsafe {
            let length = record_length_of(std::slice::from_raw_parts(self.0, RECORD_HEADER_LENGTH));
            BufferedNode::new(std::slice::from_raw_parts(self.0, length))
        }
    }
}

/// A node read in place from its buffer record.
#[derive(Clone, Copy)]
pub struct BufferedNode<'a> {
    record: &'a [u8],
}

impl<'a> BufferedNode<'a> {
    pub fn new(record: &'a [u8]) -> BufferedNode<'a> {
        BufferedNode { record }
    }

    pub fn kind(&self) -> NodeKind {
        match self.record[0] {
            LEAF => NodeKind::Leaf,
            EXTENSION => NodeKind::Extension,
            _ => NodeKind::Branch,
        }
    }

    pub fn hash(&self) -> &'a [u8; 32] {
        self.record[8..40].try_into().unwrap()
    }

    pub fn buffer_key(&self) -> u64 {
        u64::from_le_bytes(self.record[40..RECORD_HEADER_LENGTH].try_into().unwrap())
    }

    pub fn rlp(&self) -> &'a [u8] {
        &self.record
            [RECORD_HEADER_LENGTH + POINTER_LENGTH * self.hash_mask().count_ones() as usize..]
    }

    /// The key nibbles of a leaf or an extension, empty for a branch.
    pub fn key(&self) -> Nibbles {
        match self.kind() {
            NodeKind::Branch => Nibbles::from_slice(&[]),
            _ => {
                let (key, _) = buffered_item(self.payload());
                decode_hex_prefix(key.payload)
                    .expect("buffered RLP is well formed")
                    .0
            }
        }
    }

    /// The value of a leaf.
    pub fn value(&self) -> &'a [u8] {
        let (_, rest) = buffered_item(self.payload());
        buffered_item(rest).0.payload
    }

    /// Child `index` of a branch, or the child of an extension.
    pub fn child(&self, index: u8) -> ChildRef {
        let mask = self.hash_mask();
        let position = (mask & ((1 << index) - 1)).count_ones() as usize;
        match self.record[0] {
            HASHED_BRANCH => {
                if mask & (1 << index) == 0 {
                    return ChildRef::Empty;
                }
                // Every child before is an empty string or a hash string of 33 bytes.
                let start = index as usize + 32 * position + 1;
                ChildRef::Hash {
                    hash: self.payload()[start..start + 32].try_into().unwrap(),
                    pointer: self.pointer(position),
                }
            }
            INLINE_BRANCH => {
                let mut remaining = self.payload();
                for _ in 0..index {
                    remaining = buffered_item(remaining).1;
                }
                self.read_child(&buffered_item(remaining).0, position)
            }
            EXTENSION => self.read_child(&buffered_item(buffered_item(self.payload()).1).0, 0),
            _ => unreachable!("a leaf has no children"),
        }
    }

    /// Copies the node out, to be changed, less the first `skip` nibbles of the key of a leaf or an extension.
    pub fn to_owned(&self, skip: usize) -> Node {
        match self.kind() {
            NodeKind::Leaf => Node::Leaf {
                key: Nibbles::from_slice(&self.key().as_slice()[skip..]),
                value: self.value().to_vec(),
            },
            NodeKind::Extension => Node::Extension {
                key: Nibbles::from_slice(&self.key().as_slice()[skip..]),
                child: self.child(0),
            },
            NodeKind::Branch => {
                let mut children = empty_children();
                let mut remaining = self.payload();
                let mut position = 0;
                for child in children.iter_mut() {
                    let (item, rest) = buffered_item(remaining);
                    remaining = rest;
                    *child = self.read_child(&item, position);
                    if matches!(child, ChildRef::Hash { .. }) {
                        position += 1;
                    }
                }
                Node::Branch { children }
            }
        }
    }

    pub(crate) fn hash_mask(&self) -> u16 {
        u16::from_le_bytes(self.record[2..4].try_into().unwrap())
    }

    /// The pointer of the child at `position` among those referenced by hash.
    pub(crate) fn pointer(&self, position: usize) -> BufferPointer {
        let start = RECORD_HEADER_LENGTH + POINTER_LENGTH * position;
        u64::from_le_bytes(
            self.record[start..start + POINTER_LENGTH]
                .try_into()
                .unwrap(),
        )
    }

    fn payload(&self) -> &'a [u8] {
        let rlp = self.rlp();
        match rlp[0] {
            0xc0..=0xf7 => &rlp[1..],
            first => &rlp[1 + (first - 0xf7) as usize..],
        }
    }

    /// The child `item`, at `position` among those referenced by hash when it is one.
    fn read_child(&self, item: &Item, position: usize) -> ChildRef {
        match (item.is_list, item.payload.len()) {
            (true, _) => ChildRef::inline(item.encoded),
            (false, 0) => ChildRef::Empty,
            _ => ChildRef::Hash {
                hash: item.payload.try_into().unwrap(),
                pointer: self.pointer(position),
            },
        }
    }
}

fn buffered_item(data: &[u8]) -> (Item<'_>, &[u8]) {
    next_item(data).expect("buffered RLP is well formed")
}

/// Appends the RLP of `node` to `out`.
pub fn encode(node: &Node, out: &mut Vec<u8>) {
    match node {
        Node::Leaf { key, value } => {
            write_list_header(
                out,
                hex_prefix_length(key.as_slice()) + string_length(value),
            );
            write_hex_prefix(out, key.as_slice(), true);
            write_string(out, value);
        }
        Node::Extension { key, child } => {
            write_list_header(
                out,
                hex_prefix_length(key.as_slice()) + child.encoded_length(),
            );
            write_hex_prefix(out, key.as_slice(), false);
            child.write(out);
        }
        Node::Branch { children } => {
            // Room for every child as a hash, the value, and a whole inline buffer past the end, so each child is a
            // fixed-size copy.
            let mut payload = [0u8; 16 * 33 + 1 + 31];
            let mut payload_length = 0;
            for child in children.iter() {
                match child {
                    ChildRef::Empty => {
                        payload[payload_length] = EMPTY_STRING;
                        payload_length += 1;
                    }
                    ChildRef::Hash { hash, .. } => {
                        payload[payload_length] = HASH_PREFIX;
                        payload[payload_length + 1..payload_length + 33].copy_from_slice(hash);
                        payload_length += 33;
                    }
                    ChildRef::Inline(rlp) => {
                        payload[payload_length..payload_length + 31].copy_from_slice(&rlp.data);
                        payload_length += rlp.length as usize;
                    }
                    ChildRef::Pending(_) => {
                        unreachable!("pending child must be committed before encoding")
                    }
                }
            }
            payload[payload_length] = EMPTY_STRING;
            payload_length += 1;

            write_list_header(out, payload_length);
            out.extend_from_slice(&payload[..payload_length]);
        }
    }
}

pub fn decode_rlp(rlp: &[u8]) -> Result<Node, Error> {
    let (list, _) = next_item(rlp)?;
    if !list.is_list {
        return Err(Error::CorruptNode);
    }
    let (first, rest) = next_item(list.payload)?;
    let (second, rest) = next_item(rest)?;

    if !rest.is_empty() {
        let mut children: Box<[ChildRef; 16]> = empty_children();
        let mut remaining = list.payload;
        for child in children.iter_mut() {
            // Children are nearly always empty or a hash, so those skip the general item parsing.
            remaining = match remaining {
                [EMPTY_STRING, rest @ ..] => rest,
                [HASH_PREFIX, hash @ ..] if hash.len() >= 32 => {
                    *child = ChildRef::Hash {
                        hash: hash[..32].try_into().unwrap(),
                        pointer: 0,
                    };
                    &hash[32..]
                }
                _ => {
                    let (item, rest) = next_item(remaining)?;
                    *child = child_ref(&item)?;
                    rest
                }
            };
        }
        return Ok(Node::Branch { children });
    }

    if first.is_list {
        return Err(Error::CorruptNode);
    }
    let (key, is_leaf) = decode_hex_prefix(first.payload)?;
    if is_leaf {
        Ok(Node::Leaf {
            key,
            value: second.payload.to_vec(),
        })
    } else {
        Ok(Node::Extension {
            key,
            child: child_ref(&second)?,
        })
    }
}

/// Decodes the key and value of an inline child, which must be a leaf, as snap sync assumes too: a branch or an
/// extension under 32 bytes needs two keys sharing 224 bits, which keccak keys never do.
pub fn decode_inline_leaf(rlp: &[u8]) -> Result<(Nibbles, &[u8]), Error> {
    let (list, _) = next_item(rlp)?;
    if !list.is_list {
        return Err(Error::CorruptNode);
    }
    let (first, rest) = next_item(list.payload)?;
    let (second, rest) = next_item(rest)?;
    if first.is_list || !rest.is_empty() {
        return Err(Error::CorruptNode);
    }
    match decode_hex_prefix(first.payload)? {
        (key, true) => Ok((key, second.payload)),
        _ => Err(Error::CorruptNode),
    }
}

struct Item<'a> {
    is_list: bool,
    payload: &'a [u8],
    encoded: &'a [u8],
}

fn next_item(data: &[u8]) -> Result<(Item<'_>, &[u8]), Error> {
    let &first = data.first().ok_or(Error::CorruptNode)?;
    let (is_list, header_length, payload_length) = match first {
        0x00..=0x7f => (false, 0, 1),
        0x80..=0xb7 => (false, 1, (first - 0x80) as usize),
        0xb8..=0xbf => (
            false,
            1 + (first - 0xb7) as usize,
            read_length(data, (first - 0xb7) as usize)?,
        ),
        0xc0..=0xf7 => (true, 1, (first - 0xc0) as usize),
        0xf8..=0xff => (
            true,
            1 + (first - 0xf7) as usize,
            read_length(data, (first - 0xf7) as usize)?,
        ),
    };
    let end = header_length + payload_length;
    if end > data.len() {
        return Err(Error::CorruptNode);
    }
    Ok((
        Item {
            is_list,
            payload: &data[header_length..end],
            encoded: &data[..end],
        },
        &data[end..],
    ))
}

fn read_length(data: &[u8], length_of_length: usize) -> Result<usize, Error> {
    let bytes = data
        .get(1..1 + length_of_length)
        .ok_or(Error::CorruptNode)?;
    if bytes.len() > 4 {
        return Err(Error::CorruptNode);
    }
    Ok(bytes
        .iter()
        .fold(0usize, |length, &byte| (length << 8) | byte as usize))
}

fn child_ref(item: &Item) -> Result<ChildRef, Error> {
    if item.is_list {
        if item.encoded.len() >= 32 {
            return Err(Error::CorruptNode);
        }
        decode_inline_leaf(item.encoded)?;
        return Ok(ChildRef::inline(item.encoded));
    }
    match item.payload.len() {
        0 => Ok(ChildRef::Empty),
        32 => Ok(ChildRef::Hash {
            hash: item.payload.try_into().unwrap(),
            pointer: 0,
        }),
        _ => Err(Error::CorruptNode),
    }
}

/// Length of the RLP string holding the hex-prefix encoding of `nibbles`. That encoding is at most 33 bytes and its
/// first byte is below 0x80, so a single byte is its own string.
fn hex_prefix_length(nibbles: &[u8]) -> usize {
    match nibbles.len() / 2 + 1 {
        1 => 1,
        length => 1 + length,
    }
}

/// Appends the RLP string holding the hex-prefix encoding of `nibbles`. Written straight to `out`, because building
/// it in a buffer byte by byte and copying that stalls on store forwarding.
fn write_hex_prefix(out: &mut Vec<u8>, nibbles: &[u8], is_leaf: bool) {
    if nibbles.len() >= 2 {
        out.push(0x80 + (nibbles.len() / 2 + 1) as u8);
    }
    let flag = if is_leaf { 0x20 } else { 0x00 };
    let rest = if nibbles.len() % 2 == 1 {
        out.push(flag | 0x10 | nibbles[0]);
        &nibbles[1..]
    } else {
        out.push(flag);
        nibbles
    };
    out.extend(rest.chunks_exact(2).map(|pair| (pair[0] << 4) | pair[1]));
}

fn decode_hex_prefix(bytes: &[u8]) -> Result<(Nibbles, bool), Error> {
    let &first = bytes.first().ok_or(Error::CorruptNode)?;
    let is_odd = first & 0x10 != 0;
    let length = (bytes.len() - 1) * 2 + is_odd as usize;
    if length > 64 {
        return Err(Error::CorruptNode);
    }
    let mut nibbles = Nibbles {
        length: length as u8,
        data: [0; 64],
    };
    let mut index = 0;
    if is_odd {
        nibbles.data[0] = first & 0x0f;
        index = 1;
    }
    for &byte in &bytes[1..] {
        nibbles.data[index] = byte >> 4;
        nibbles.data[index + 1] = byte & 0x0f;
        index += 2;
    }
    Ok((nibbles, first & 0x20 != 0))
}

fn string_length(bytes: &[u8]) -> usize {
    match bytes.len() {
        1 if bytes[0] < 0x80 => 1,
        length if length < 56 => 1 + length,
        length => 1 + length_of_length(length) + length,
    }
}

fn write_string(out: &mut Vec<u8>, bytes: &[u8]) {
    match bytes.len() {
        1 if bytes[0] < 0x80 => {}
        length if length < 56 => out.push(0x80 + length as u8),
        length => write_long_header(out, 0xb7, length),
    }
    out.extend_from_slice(bytes);
}

fn write_list_header(out: &mut Vec<u8>, payload_length: usize) {
    if payload_length < 56 {
        out.push(0xc0 + payload_length as u8);
    } else {
        write_long_header(out, 0xf7, payload_length);
    }
}

fn write_long_header(out: &mut Vec<u8>, base: u8, length: usize) {
    let length_of_length = length_of_length(length);
    out.push(base + length_of_length as u8);
    out.extend_from_slice(&length.to_be_bytes()[std::mem::size_of::<usize>() - length_of_length..]);
}

fn length_of_length(length: usize) -> usize {
    (usize::BITS - length.leading_zeros()).div_ceil(8) as usize
}
