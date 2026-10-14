//! Value types shared by the persistence layer: ports of `Nethermind.State.Flat/StateId.cs`, the parts of
//! `Nethermind.Trie/TreePath.cs` the flat keys use, `Nethermind.Core/Account.cs` and the `Nethermind.Int256.UInt256`
//! operations the codecs need.

use std::cmp::Ordering;
use std::fmt;

/// A 20-byte account address.
pub type Address = [u8; 20];

/// A 32-byte hash (`ValueHash256`). Compares byte-wise, as `ValueHash256.CompareTo` does.
pub type Hash256 = [u8; 32];

/// Decodes a 64-digit hex string at compile time.
const fn hash_from_hex(hex: &str) -> Hash256 {
    let bytes = hex.as_bytes();
    assert!(bytes.len() == 64, "a hash has 64 hex digits");
    let mut hash = [0u8; 32];
    let mut i = 0;
    while i < 32 {
        hash[i] = (nibble(bytes[2 * i]) << 4) | nibble(bytes[2 * i + 1]);
        i += 1;
    }
    hash
}

const fn nibble(digit: u8) -> u8 {
    match digit {
        b'0'..=b'9' => digit - b'0',
        b'a'..=b'f' => digit - b'a' + 10,
        b'A'..=b'F' => digit - b'A' + 10,
        _ => panic!("not a hex digit"),
    }
}

/// `Keccak.EmptyTreeHash`, the root of an empty trie.
pub const EMPTY_TREE_HASH: Hash256 = hash_from_hex("56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");

/// `Keccak.OfAnEmptyString`, the code hash of an account without code.
pub const KECCAK_OF_EMPTY_STRING: Hash256 =
    hash_from_hex("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");

/// A 256-bit unsigned integer with the limb order of `Nethermind.Int256.UInt256`: `0` is the least significant.
#[derive(Clone, Copy, Default, PartialEq, Eq, Hash)]
pub struct U256(pub [u64; 4]);

impl U256 {
    pub const ZERO: Self = Self([0; 4]);
    pub const MAX: Self = Self([u64::MAX; 4]);

    pub const fn from_u64(value: u64) -> Self {
        Self([value, 0, 0, 0])
    }

    pub const fn is_zero(&self) -> bool {
        self.0[0] == 0 && self.0[1] == 0 && self.0[2] == 0 && self.0[3] == 0
    }

    /// Whether the value fits in a `u64`.
    pub const fn is_u64(&self) -> bool {
        self.0[1] == 0 && self.0[2] == 0 && self.0[3] == 0
    }

    /// Reads a big-endian value of at most 32 bytes, as `new UInt256(bytes, isBigEndian: true)` does.
    ///
    /// # Panics
    /// When `bytes` is longer than 32 bytes; callers check the length first, as C# does.
    pub fn from_be_slice(bytes: &[u8]) -> Self {
        assert!(bytes.len() <= 32, "a UInt256 has at most 32 bytes, got {}", bytes.len());
        let mut padded = [0u8; 32];
        padded[32 - bytes.len()..].copy_from_slice(bytes);
        Self::from_be_bytes(&padded)
    }

    pub fn from_be_bytes(bytes: &[u8; 32]) -> Self {
        let limb = |index: usize| {
            let start = 32 - 8 * (index + 1);
            u64::from_be_bytes(bytes[start..start + 8].try_into().expect("8 bytes"))
        };
        Self([limb(0), limb(1), limb(2), limb(3)])
    }

    pub fn to_be_bytes(&self) -> [u8; 32] {
        let mut bytes = [0u8; 32];
        for (index, limb) in self.0.iter().enumerate() {
            let start = 32 - 8 * (index + 1);
            bytes[start..start + 8].copy_from_slice(&limb.to_be_bytes());
        }
        bytes
    }

    /// The shortest non-empty big-endian form, as `UInt256Extensions.ToMinimalBigEndian` returns it: zero is one zero
    /// byte. Returns the full big-endian bytes and the offset where the minimal form starts.
    pub fn to_minimal_be(&self) -> ([u8; 32], usize) {
        let bytes = self.to_be_bytes();
        let start = bytes.iter().position(|&byte| byte != 0).unwrap_or(31);
        (bytes, start)
    }

    /// The shortest big-endian form without any leading zero byte; empty for zero. This is the RLP integer payload.
    pub fn to_be_trimmed(&self) -> ([u8; 32], usize) {
        let bytes = self.to_be_bytes();
        let start = bytes.iter().position(|&byte| byte != 0).unwrap_or(32);
        (bytes, start)
    }
}

impl From<u64> for U256 {
    fn from(value: u64) -> Self {
        Self::from_u64(value)
    }
}

impl Ord for U256 {
    fn cmp(&self, other: &Self) -> Ordering {
        self.0.iter().rev().cmp(other.0.iter().rev())
    }
}

impl PartialOrd for U256 {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl fmt::Debug for U256 {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "0x")?;
        for byte in self.to_be_bytes() {
            write!(f, "{byte:02x}")?;
        }
        Ok(())
    }
}

/// A persisted state, ordered by block number then root. Port of `StateId.cs`.
#[derive(Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct StateId {
    pub block_number: u64,
    pub state_root: Hash256,
}

impl StateId {
    /// `StateId.PreGenesis`: the state of a database without a persisted state pointer.
    pub const PRE_GENESIS: Self = Self::new(u64::MAX, EMPTY_TREE_HASH);

    /// `StateId.Sync`: a write that does not move the persisted state pointer.
    pub const SYNC: Self = Self::new(u64::MAX - 1, EMPTY_TREE_HASH);

    pub const fn new(block_number: u64, state_root: Hash256) -> Self {
        Self {
            block_number,
            state_root,
        }
    }
}

impl fmt::Debug for StateId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "StateId({}, 0x", self.block_number)?;
        for byte in self.state_root {
            write!(f, "{byte:02x}")?;
        }
        write!(f, ")")
    }
}

/// As C# formats the `StateId` record, for log lines that match the C# ones.
impl fmt::Display for StateId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "StateId {{ BlockNumber = {}, StateRoot = 0x", self.block_number)?;
        for byte in self.state_root {
            write!(f, "{byte:02x}")?;
        }
        write!(f, " }}")
    }
}

/// A trie node path of up to 64 nibbles, packed two per byte. Port of the parts of `TreePath.cs` the flat keys use.
///
/// Like `TreePath`, it keeps whatever bytes the caller passed past `length`: the key encodings copy them, so a path
/// that is not zero past its length encodes differently from one that is, in both implementations.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct TreePath {
    pub path: Hash256,
    pub length: u8,
}

impl TreePath {
    /// The most nibbles a path holds.
    pub const MAX_LENGTH: u8 = 64;

    /// # Panics
    /// When `length` exceeds 64, as `new TreePath(path, length)` throws.
    pub fn new(path: Hash256, length: u8) -> Self {
        assert!(
            length <= Self::MAX_LENGTH,
            "TreePath can't represent more than 64 nibble."
        );
        Self { path, length }
    }

    /// Port of `TreePath.FromHexString`: the nibbles of `hex`, zero-padded to 64.
    ///
    /// # Panics
    /// On a non-hex digit or more than 64 digits.
    pub fn from_hex(hex: &str) -> Self {
        let hex = hex.strip_prefix("0x").unwrap_or(hex);
        assert!(hex.len() <= 64, "a path has at most 64 nibbles");
        let mut path = [0u8; 32];
        for (index, digit) in hex.bytes().enumerate() {
            let value = nibble(digit);
            path[index / 2] |= if index % 2 == 0 { value << 4 } else { value };
        }
        Self::new(path, hex.len() as u8)
    }

    /// `TreePath.ToLowerBoundPath`: the path itself, its remaining nibbles being zero.
    pub const fn to_lower_bound_path(&self) -> Hash256 {
        self.path
    }

    /// `TreePath.ToUpperBoundPath`: the path extended to 64 nibbles with `0xF`, the upper bound of its subtree.
    pub fn to_upper_bound_path(&self) -> Hash256 {
        let mut result = self.path;
        let mut start = usize::from(self.length) / 2;
        if self.length % 2 == 1 {
            result[start] |= 0x0F;
            start += 1;
        }
        result[start..].fill(0xFF);
        result
    }

    /// `TreePath.EncodeWith3Byte`: the first three path bytes with the length in the low nibble of the last one.
    pub fn encode_with_3_byte(&self, buffer: &mut [u8]) {
        self.encode_packed::<3>(buffer);
    }

    /// `TreePath.EncodeWith4Byte`: the first four path bytes with the length in the low nibble of the last one.
    pub fn encode_with_4_byte(&self, buffer: &mut [u8]) {
        self.encode_packed::<4>(buffer);
    }

    /// `TreePath.EncodeWith8Byte`: the first eight path bytes with the length in the low nibble of the last one.
    pub fn encode_with_8_byte(&self, buffer: &mut [u8]) {
        self.encode_packed::<8>(buffer);
    }

    /// `TreePath.DecodeWith4Byte`: the inverse of [`Self::encode_with_4_byte`], with the length nibble cleared.
    pub fn decode_with_4_byte(buffer: &[u8]) -> Self {
        Self::decode_packed::<4>(buffer)
    }

    /// `TreePath.DecodeWith8Byte`: the inverse of [`Self::encode_with_8_byte`], with the length nibble cleared.
    pub fn decode_with_8_byte(buffer: &[u8]) -> Self {
        Self::decode_packed::<8>(buffer)
    }

    fn encode_packed<const N: usize>(&self, buffer: &mut [u8]) {
        buffer[..N].copy_from_slice(&self.path[..N]);
        buffer[N - 1] = (buffer[N - 1] & 0xF0) | (self.length & 0x0F);
    }

    fn decode_packed<const N: usize>(buffer: &[u8]) -> Self {
        let mut path = [0u8; 32];
        path[..N].copy_from_slice(&buffer[..N]);
        let length = path[N - 1] & 0x0F;
        path[N - 1] &= 0xF0;
        Self::new(path, length)
    }
}

impl fmt::Debug for TreePath {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut hex = String::with_capacity(64);
        for byte in self.path {
            hex.push_str(&format!("{byte:02x}"));
        }
        write!(f, "TreePath({}/{})", &hex[..usize::from(self.length)], self.length)
    }
}

/// An account. Port of `Account.cs`: an empty storage root and an empty code hash are the absent ones.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct Account {
    pub nonce: u64,
    pub balance: U256,
    pub storage_root: Hash256,
    pub code_hash: Hash256,
}

impl Account {
    /// An account without storage or code, as `new Account(nonce, balance)`.
    pub const fn new(nonce: u64, balance: U256) -> Self {
        Self {
            nonce,
            balance,
            storage_root: EMPTY_TREE_HASH,
            code_hash: KECCAK_OF_EMPTY_STRING,
        }
    }

    pub const fn with_hashes(nonce: u64, balance: U256, storage_root: Hash256, code_hash: Hash256) -> Self {
        Self {
            nonce,
            balance,
            storage_root,
            code_hash,
        }
    }

    pub fn has_storage(&self) -> bool {
        self.storage_root != EMPTY_TREE_HASH
    }

    pub fn has_code(&self) -> bool {
        self.code_hash != KECCAK_OF_EMPTY_STRING
    }
}

/// The on-disk layout of the flat DB, with the values of `Nethermind.Db.FlatLayout`.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum FlatLayout {
    Flat = 0,
    FlatInTrie = 1,
    PreimageFlatV1 = 2,
    PreimageFlat = 3,
}

impl FlatLayout {
    pub const ALL: [FlatLayout; 4] = [
        FlatLayout::Flat,
        FlatLayout::FlatInTrie,
        FlatLayout::PreimageFlatV1,
        FlatLayout::PreimageFlat,
    ];

    /// The layout with the given `FlatLayout` value, if there is one.
    pub fn from_u8(value: u8) -> Option<Self> {
        Self::ALL.get(usize::from(value)).copied()
    }

    /// Whether the layout keys flat state by address and slot instead of their hashes.
    pub fn is_preimage(self) -> bool {
        matches!(self, FlatLayout::PreimageFlatV1 | FlatLayout::PreimageFlat)
    }
}

impl fmt::Display for FlatLayout {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

#[cfg(test)]
mod tests {
    use super::{Account, FlatLayout, StateId, TreePath, U256};
    use crate::keccak::keccak256;
    use rstest::rstest;

    fn hex32(hex: &str) -> [u8; 32] {
        let hex = hex.strip_prefix("0x").unwrap_or(hex);
        let mut bytes = [0u8; 32];
        for (index, byte) in bytes.iter_mut().enumerate() {
            *byte = u8::from_str_radix(&hex[2 * index..2 * index + 2], 16).unwrap();
        }
        bytes
    }

    #[test]
    fn empty_hashes_are_the_keccak_constants() {
        assert_eq!(super::KECCAK_OF_EMPTY_STRING, keccak256(&[]));
        assert_eq!(super::EMPTY_TREE_HASH, keccak256(&[0x80]));
    }

    #[rstest]
    #[case("", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    #[case("ab", "0xabffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    #[case("abc", "0xabcfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    #[case(
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    )]
    fn upper_bound_fills_the_subtree_with_f(#[case] path: &str, #[case] upper: &str) {
        let path = TreePath::from_hex(path);
        assert_eq!(path.to_upper_bound_path(), hex32(upper));
        assert_eq!(path.to_lower_bound_path(), path.path);
    }

    #[rstest]
    #[case("12345", &[0x12, 0x34, 0x55])]
    #[case("1", &[0x10, 0x00, 0x01])]
    #[case("", &[0x00, 0x00, 0x00])]
    fn encode_with_3_byte_packs_the_length(#[case] path: &str, #[case] expected: &[u8]) {
        let mut buffer = [0u8; 3];
        TreePath::from_hex(path).encode_with_3_byte(&mut buffer);
        assert_eq!(buffer, expected);
    }

    #[test]
    fn encode_with_8_byte_packs_the_length_and_keeps_bytes_past_it() {
        let mut buffer = [0u8; 8];
        TreePath::from_hex("123456789abcdef").encode_with_8_byte(&mut buffer);
        assert_eq!(buffer, [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xff]);

        // Like TreePath, a path with garbage past its length keeps it.
        let dirty = TreePath::new([0xEE; 32], 6);
        dirty.encode_with_8_byte(&mut buffer);
        assert_eq!(buffer, [0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xE6]);
    }

    #[rstest]
    #[case("1234567", &[0x12, 0x34, 0x56, 0x77])]
    #[case("", &[0x00, 0x00, 0x00, 0x00])]
    fn four_byte_form_packs_the_length_and_decodes_with_the_nibble_cleared(
        #[case] path: &str,
        #[case] expected: &[u8],
    ) {
        let mut buffer = [0u8; 4];
        TreePath::from_hex(path).encode_with_4_byte(&mut buffer);
        assert_eq!(buffer, expected);
        assert_eq!(TreePath::decode_with_4_byte(&buffer), TreePath::from_hex(path));

        let mut wide = [0u8; 8];
        TreePath::from_hex("123456789abcdef").encode_with_8_byte(&mut wide);
        assert_eq!(
            TreePath::decode_with_8_byte(&wide),
            TreePath::from_hex("123456789abcdef")
        );
        TreePath::new([0xEE; 32], 3).encode_with_4_byte(&mut buffer);
        assert_eq!(buffer, [0xEE, 0xEE, 0xEE, 0xE3]);
        assert_eq!(
            TreePath::decode_with_4_byte(&buffer).path[..4],
            [0xEE, 0xEE, 0xEE, 0xE0]
        );
    }

    #[test]
    #[should_panic(expected = "more than 64 nibble")]
    fn tree_path_rejects_more_than_64_nibbles() {
        TreePath::new([0; 32], 65);
    }

    #[test]
    fn u256_round_trips_and_orders_by_value() {
        let value = U256::from_be_slice(&[0x01, 0x02, 0x03]);
        assert_eq!(value, U256::from_u64(0x010203));
        assert_eq!(U256::from_be_bytes(&value.to_be_bytes()), value);
        let (bytes, start) = value.to_minimal_be();
        assert_eq!(&bytes[start..], &[0x01, 0x02, 0x03]);
        let (zero, start) = U256::ZERO.to_minimal_be();
        assert_eq!(&zero[start..], &[0x00], "zero's minimal form is one zero byte");
        let (zero, start) = U256::ZERO.to_be_trimmed();
        assert!(zero[start..].is_empty());
        assert!(U256([0, 0, 0, 1]) > U256([u64::MAX, u64::MAX, u64::MAX, 0]));
        assert!(U256::MAX > U256::ZERO);
    }

    #[test]
    fn state_ids_order_by_block_then_root() {
        assert!(StateId::new(1, [0xff; 32]) < StateId::new(2, [0; 32]));
        assert!(StateId::new(1, [0; 32]) < StateId::new(1, [1; 32]));
        assert!(StateId::SYNC < StateId::PRE_GENESIS);
        assert_eq!(
            StateId::new(
                5,
                hex32("0x0a00000000000000000000000000000000000000000000000000000000000001")
            )
            .to_string(),
            "StateId { BlockNumber = 5, StateRoot = 0x0a00000000000000000000000000000000000000000000000000000000000001 }"
        );
    }

    #[test]
    fn accounts_and_layouts() {
        let account = Account::new(1, U256::from_u64(2));
        assert!(!account.has_code() && !account.has_storage());
        assert_eq!(FlatLayout::from_u8(3), Some(FlatLayout::PreimageFlat));
        assert_eq!(FlatLayout::from_u8(4), None);
        assert!(FlatLayout::PreimageFlatV1.is_preimage() && !FlatLayout::FlatInTrie.is_preimage());
    }
}
