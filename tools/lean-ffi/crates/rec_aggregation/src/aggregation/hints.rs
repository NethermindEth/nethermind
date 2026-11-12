//! Hint streams and the raw signatures' witnesses.

use super::cells::{count, exponent_bytes, pack_16_bytes};
use super::error::AggregationError;
use leanvm_core::cpu::Program;
use primitives::field::F192;
use signatures::lean_sphincs::{LeanSphincsPublicKey, LeanSphincsSignature};
use signatures::lean_xmss::{LeanXmssPublicKey, LeanXmssSignature};
use signatures::{lean_sphincs, lean_xmss};

// Both schemes' one-time keys go through the guest's one WOTS+C walk: the same chain
// length and digits dispatched a group at a time. leanSPHINCS groups DIGIT_GROUP
// digits, the last chain alone; leanXMSS LX_DIGIT_GROUP, then the rest. A chain step's
// address leaves out the chain index in leanXMSS, where it follows the value.
pub(super) const DIGIT_GROUP: usize = 3;
/// The guest's `LX_DIGIT_GROUP`.
pub(super) const LX_DIGIT_GROUP: usize = 4;
/// The guest's `LX_WINDOW`: index bits a path dispatch covers.
pub(super) const LX_WINDOW: usize = 8;
const _: () = assert!(lean_sphincs::W * lean_sphincs::V == 128 && lean_sphincs::V % DIGIT_GROUP == 1);
const _: () = assert!(lean_xmss::W == lean_sphincs::W && !lean_xmss::V.is_multiple_of(LX_DIGIT_GROUP));
// A leaf index is the two layers' indices, which the guest dispatches on a window at a time.
const _: () = assert!(lean_xmss::LOG_LIFETIME == 32 && lean_xmss::LAYER_HEIGHT.is_multiple_of(LX_WINDOW));
// The guest reads the leanSPHINCS digest as this shape lays it out: the FORS leaves
// fill the first call's first 30 bytes, each byte in at most two of them, the tree
// index's three bytes follow, the last in the second call, then the bottom leaf.
// It reads the layers' leaves off those bytes, and places a FORS tree index in two
// bytes and a codeword's digits a whole number to a byte.
const _: () = assert!(lean_sphincs::LAYER_HEIGHTS[0] == 8 && lean_sphincs::LAYER_HEIGHTS[1] == 8);
const _: () = assert!(lean_sphincs::LAYER_HEIGHTS[2] == 10 && lean_sphincs::LAYERS == 3);
const _: () = assert!(lean_sphincs::MD_BYTES == 30 && lean_sphincs::FORS_TREES * lean_sphincs::FORS_HEIGHT == 240);
const _: () =
    assert!(lean_sphincs::FORS_HEIGHT == 10 && lean_sphincs::FORS_TREES << lean_sphincs::FORS_HEIGHT <= 1 << 16);
const _: () = assert!(lean_sphincs::TREE_INDEX_BYTES == 3 && lean_sphincs::LEAF_INDEX_BYTES == 1);
const _: () = assert!(lean_sphincs::DIGEST_CALLS == 2 && 8 % lean_sphincs::W == 0);

/// The end of `H_msg`'s second call's low lane: its bytes past the digest are the
/// guest's `SP_UNUSED_BYTES`, `8 - (DIGEST_BYTES - 32)`.
const SP_LANE_END: usize = 32 + 8;
const _: () = assert!(lean_sphincs::DIGEST_BYTES > 32 && lean_sphincs::DIGEST_BYTES <= SP_LANE_END);

/// Named hint entries for a single sub-proof, ordered within each stream.
pub(super) type SubHints = Vec<(&'static str, Vec<F192>)>;

/// An edit to a node's hints: how a test hands the guest a witness the honest host
/// would not.
#[cfg(test)]
pub(super) type Forgery = Box<dyn FnOnce(&mut Hints)>;

#[cfg(test)]
thread_local! {
    /// The forgery the next node this thread aggregates gets.
    pub(super) static FORGE: std::cell::RefCell<Option<Forgery>> = const { std::cell::RefCell::new(None) };
}

/// One `hint_witness` stream: a name and its entries, in the order the guest
/// pops them.
#[derive(Default)]
pub(super) struct Hints(Vec<(String, Vec<Vec<F192>>)>);

impl Hints {
    pub(super) fn push(&mut self, name: &str, entry: Vec<F192>) {
        match self.0.iter_mut().find(|(n, _)| n == name) {
            Some((_, entries)) => entries.push(entry),
            None => self.0.push((name.to_string(), vec![entry])),
        }
    }

    /// Entry `i` of stream `name`, for a test to forge.
    #[cfg(test)]
    pub(super) fn entry_mut(&mut self, name: &str, i: usize) -> &mut Vec<F192> {
        let (_, entries) = self.0.iter_mut().find(|(n, _)| n == name).expect("a hinted stream");
        &mut entries[i]
    }

    pub(super) fn install(self, guest: &mut Program) {
        for (name, entries) in self.0 {
            guest.set_witness(name, entries);
        }
    }
}

/// The digits of a WOTS+C codeword as the guest dispatches on them: `group` at a
/// time, then the rest, each group's index little endian in base `CHAIN_LEN`, in the
/// exponent.
fn digit_hints(codeword: &[u8], size: usize) -> impl Iterator<Item = Vec<F192>> + '_ {
    codeword.chunks(size).map(|group| {
        let index = group
            .iter()
            .rev()
            .fold(0, |index, &digit| index * lean_xmss::CHAIN_LEN + digit as usize);
        vec![count(index)]
    })
}

/// One leanXMSS signature's witness: its leaf index shifted by each multiple of
/// `LX_WINDOW` and those windows in the exponent, then per layer, bottom first, the
/// randomizer, the codeword's digits, the chain values they start from, and the
/// sibling of every node on the path.
pub(super) fn lean_xmss_hints(
    pk: &LeanXmssPublicKey,
    sig: &LeanXmssSignature,
    message: &lean_xmss::Message,
) -> Result<SubHints, AggregationError> {
    let pp = pk.public_param();
    let index = u64::from(sig.leaf_index());
    let windows = (0..lean_xmss::LOG_LIFETIME).step_by(LX_WINDOW).map(|s| index >> s);
    let mut hints: SubHints = vec![
        ("lx_index", windows.clone().map(|x| F192::new(x, 0, 0)).collect()),
        (
            "lx_windows",
            windows.map(|x| count((x % (1 << LX_WINDOW)) as usize)).collect(),
        ),
    ];
    let mut root: Vec<u8> = message.to_vec();
    let mut level = 0;
    for (layer, layer_sig) in [sig.bottom(), sig.top()].into_iter().enumerate() {
        let e = lean_xmss::layer_index(sig.leaf_index(), layer);
        let (codeword, reached) = lean_xmss::layer_root(pp, layer, e, &root, layer_sig, |pair| {
            hints.push((
                "lx_path",
                vec![pack_16_bytes(&pair[usize::from(index >> level & 1 == 0)])],
            ));
            level += 1;
        })
        .ok_or(AggregationError::MalformedRawSignature)?;
        hints.push(("randomizer", vec![pack_16_bytes(layer_sig.randomizer())]));
        hints.push(("lx_digits", digit_hints(&codeword, LX_DIGIT_GROUP).flatten().collect()));
        for value in layer_sig.chain_values() {
            hints.push(("chain_starts", vec![pack_16_bytes(value)]));
        }
        root = reached.to_vec();
    }
    Ok(hints)
}

/// One leanSPHINCS signature's witness: the randomizer, the bytes of `H_msg`'s
/// second call's low lane past the digest, each FORS tree's secret
/// and the ordered children of every node on its climb, then per layer, bottom
/// first, the encoding counter's bytes, the codeword digits (three at a time, in the
/// exponent), the chain values they start from, and the ordered children of every
/// node on the key's path.
///
/// The guest derives the index and every FORS leaf from the digest itself, so
/// nothing here carries them. The signer's message is not hinted either: it rides
/// its slot in the coverage table.
pub(super) fn lean_sphincs_hints(
    pk: &LeanSphincsPublicKey,
    sig: &LeanSphincsSignature,
    message: &lean_sphincs::Message,
) -> Result<SubHints, AggregationError> {
    let pk_seed = pk.pk_seed();
    let mut hints: SubHints = vec![("sp_rand", vec![pack_16_bytes(sig.randomizer())])];
    let digest = lean_sphincs::h_msg(sig.randomizer(), pk_seed, pk.root(), message);
    let (idx, leaves) = lean_sphincs::digest_fields(&digest);
    hints.push((
        "sp_unused",
        exponent_bytes(&digest[lean_sphincs::DIGEST_BYTES..SP_LANE_END]),
    ));
    for tree in sig.fors() {
        hints.push(("sp_fors_secrets", vec![pack_16_bytes(tree.secret())]));
    }
    // On every path the guest does not order a node and its sibling: it is handed the
    // two children in order and checks that the node it holds is the one its bit names.
    let ordered = |pair: &[lean_sphincs::Digest; 2]| vec![pack_16_bytes(&pair[0]), pack_16_bytes(&pair[1])];
    let mut signed = lean_sphincs::fors_recover(pk_seed, idx, &leaves, sig.fors(), |pair| {
        hints.push(("sp_fors_nodes", ordered(pair)));
    });
    for (layer, layer_sig) in sig.layers().iter().enumerate() {
        let key = lean_sphincs::OtsKey::of_index(layer, idx);
        let (codeword, leaf) = lean_sphincs::wots_leaf(pk_seed, &key, &signed, layer_sig.counter(), layer_sig.wots())
            .ok_or(AggregationError::MalformedRawSignature)?;
        hints.push(("sp_counter", exponent_bytes(&layer_sig.counter().to_be_bytes())));
        hints.extend(digit_hints(&codeword, DIGIT_GROUP).map(|entry| ("digits", entry)));
        for value in layer_sig.wots() {
            hints.push(("chain_starts", vec![pack_16_bytes(value)]));
        }
        signed = lean_sphincs::tree_fold(pk_seed, &key, leaf, sig.path(layer), |pair| {
            hints.push(("siblings", ordered(pair)));
        });
    }
    Ok(hints)
}
