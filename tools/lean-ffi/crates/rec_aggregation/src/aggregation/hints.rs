//! Hint streams and the raw signatures' witnesses.

use super::cells::{count, pack_16_bytes};
use super::error::AggregationError;
use leanvm_core::cpu::Program;
use primitives::field::F192;
use signatures::lean_sphincs;
use signatures::lean_sphincs::{LeanSphincsPublicKey, LeanSphincsSignature};

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

#[cfg(test)]
thread_local! {
    /// Lets the next nodes this thread aggregates take more leanSTARK claims than the cap, to see the guest refuse them.
    pub(super) static ALLOW_MANY_GENERIC: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    /// An honest leanSTARK `(data_hash, witness)` whose proof the next nodes' leanSTARK sub-proof hints
    /// come from, whatever claim and witness they are handed: how a test shows the guest a false claim.
    pub(super) static HONEST_GENERIC: std::cell::RefCell<Option<([u8; 32], Vec<u8>)>> = const { std::cell::RefCell::new(None) };
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

/// One NiceTry SPHINCS- v2 signature's witness (prototype): the randomizer, each FORS
/// tree's secret and path, then per layer the WOTS digits (in the exponent), the chain
/// values they start from, and the key's path.
pub(super) fn lean_sphincs_hints(
    pk: &LeanSphincsPublicKey,
    sig: &LeanSphincsSignature,
    message: &lean_sphincs::Message,
) -> Result<SubHints, AggregationError> {
    let trace = lean_sphincs::verify_trace(pk, message, sig);
    // Test builds only: the switch hands the guest an invalid signature, to see it fault there.
    let unchecked = cfg!(feature = "forge") && std::env::var_os("EIP8288_NO_HOST_SIGCHECK").is_some();
    if trace.signed[lean_sphincs::D] != pk.root && !unchecked {
        return Err(AggregationError::MalformedRawSignature);
    }
    let mut hints: SubHints = vec![("sp_rand", vec![pack_16_bytes(&sig.randomizer)])];
    for (secret, path) in sig.fors_secrets.iter().zip(&sig.fors_paths) {
        hints.push(("sp_fors_secrets", vec![pack_16_bytes(secret)]));
        for sibling in path {
            hints.push(("sp_fors_paths", vec![pack_16_bytes(sibling)]));
        }
    }
    for (layer, digits) in sig.layers.iter().zip(&trace.digits) {
        for (start, &digit) in layer.chains.iter().zip(digits) {
            hints.push(("sp_digits", vec![count(digit as usize)]));
            hints.push(("sp_chain_starts", vec![pack_16_bytes(start)]));
        }
        for sibling in &layer.path {
            hints.push(("sp_siblings", vec![pack_16_bytes(sibling)]));
        }
    }
    Ok(hints)
}
