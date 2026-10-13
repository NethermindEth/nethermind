// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Node model and the stored format `<tag:u8>[<bitset:u16 LE> if branch]<rlp>`, where `rlp` is the
//! standard Ethereum node RLP (so its keccak is the node hash) and bit `i` of the bitset is set when
//! branch child `i` exists.

use crate::Error;

pub const TAG_BRANCH: u8 = 0;
pub const TAG_BRANCH_WITH_INLINE: u8 = 1;
pub const TAG_EXTENSION: u8 = 2;
pub const TAG_LEAF: u8 = 3;

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
    fn new(rlp: &[u8]) -> InlineRlp {
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
    Hash([u8; 32]),
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
            ChildRef::Hash(_) => 33,
            ChildRef::Inline(rlp) => rlp.as_slice().len(),
            ChildRef::Pending(_) => unreachable!("pending child must be committed before encoding"),
        }
    }

    fn write(&self, out: &mut Vec<u8>) {
        match self {
            ChildRef::Empty => out.push(EMPTY_STRING),
            ChildRef::Hash(hash) => {
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

/// Appends the stored form of `node` to `out` and returns the offset where its RLP starts.
pub fn encode(node: &Node, out: &mut Vec<u8>) -> usize {
    match node {
        Node::Leaf { key, value } => {
            out.push(TAG_LEAF);
            let start = out.len();
            write_list_header(
                out,
                hex_prefix_length(key.as_slice()) + string_length(value),
            );
            write_hex_prefix(out, key.as_slice(), true);
            write_string(out, value);
            start
        }
        Node::Extension { key, child } => {
            out.push(TAG_EXTENSION);
            let start = out.len();
            write_list_header(
                out,
                hex_prefix_length(key.as_slice()) + child.encoded_length(),
            );
            write_hex_prefix(out, key.as_slice(), false);
            child.write(out);
            start
        }
        Node::Branch { children } => {
            // Room for every child as a hash, the value, and a whole inline buffer past the end, so each child is a
            // fixed-size copy.
            let mut payload = [0u8; 16 * 33 + 1 + 31];
            let mut payload_length = 0;
            let mut bitset = 0u16;
            let mut has_inline = false;
            for (index, child) in children.iter().enumerate() {
                match child {
                    ChildRef::Empty => {
                        payload[payload_length] = EMPTY_STRING;
                        payload_length += 1;
                        continue;
                    }
                    ChildRef::Hash(hash) => {
                        payload[payload_length] = HASH_PREFIX;
                        payload[payload_length + 1..payload_length + 33].copy_from_slice(hash);
                        payload_length += 33;
                    }
                    ChildRef::Inline(rlp) => {
                        has_inline = true;
                        payload[payload_length..payload_length + 31].copy_from_slice(&rlp.data);
                        payload_length += rlp.length as usize;
                    }
                    ChildRef::Pending(_) => {
                        unreachable!("pending child must be committed before encoding")
                    }
                }
                bitset |= 1 << index;
            }
            payload[payload_length] = EMPTY_STRING;
            payload_length += 1;

            out.push(if has_inline {
                TAG_BRANCH_WITH_INLINE
            } else {
                TAG_BRANCH
            });
            out.extend_from_slice(&bitset.to_le_bytes());
            let start = out.len();
            write_list_header(out, payload_length);
            out.extend_from_slice(&payload[..payload_length]);
            start
        }
    }
}

/// Returns the RLP part of a stored node.
pub fn stored_rlp(stored: &[u8]) -> Result<&[u8], Error> {
    match stored.first() {
        Some(&TAG_BRANCH) | Some(&TAG_BRANCH_WITH_INLINE) => {
            stored.get(3..).ok_or(Error::CorruptNode)
        }
        Some(&TAG_EXTENSION) | Some(&TAG_LEAF) => Ok(&stored[1..]),
        _ => Err(Error::CorruptNode),
    }
}

pub fn decode_stored(stored: &[u8]) -> Result<Node, Error> {
    let rlp = stored_rlp(stored)?;
    if stored[0] != TAG_BRANCH {
        return decode_rlp(rlp);
    }

    // Every child of a plain branch is a hash, so the bitset alone says where each one is.
    let bitset = u16::from_le_bytes([stored[1], stored[2]]);
    let (list, _) = next_item(rlp)?;
    let mut payload = list.payload;
    let mut children: Box<[ChildRef; 16]> = empty_children();
    for (index, child) in children.iter_mut().enumerate() {
        if bitset & (1 << index) == 0 {
            payload = payload.get(1..).ok_or(Error::CorruptNode)?;
            continue;
        }
        let hash = payload.get(1..33).ok_or(Error::CorruptNode)?;
        *child = ChildRef::Hash(hash.try_into().unwrap());
        payload = &payload[33..];
    }
    Ok(Node::Branch { children })
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
            let (item, rest) = next_item(remaining)?;
            *child = child_ref(&item)?;
            remaining = rest;
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
        return if item.encoded.len() < 32 {
            Ok(ChildRef::inline(item.encoded))
        } else {
            Err(Error::CorruptNode)
        };
    }
    match item.payload.len() {
        0 => Ok(ChildRef::Empty),
        32 => Ok(ChildRef::Hash(item.payload.try_into().unwrap())),
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
