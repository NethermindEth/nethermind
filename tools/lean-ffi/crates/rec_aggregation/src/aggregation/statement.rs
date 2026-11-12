//! Claim-list digests and a node's public statement.

use super::cells::{cell_bytes, count, pack_16_bytes};
use super::deferred::DeferredClaim;
use super::guest::unified_guest;
use super::{LeanSphincsClaim, LeanXmssClaim};
use fiat_shamir::merkle::hash_to_scalars;
use primitives::field::F192;

/// Claims the guest hashes a loop frame: 68 cells, four whole block pairs.
pub(super) const CLAIM_GROUP: usize = 17;
const _: () = assert!((CLAIM_GROUP * 64).is_multiple_of(2 * primitives::hash::RATE));
/// Groups of [`CLAIM_GROUP`] the guest's main list loop hashes a frame.
pub(super) const BIG_GROUPS: usize = 8;
const CLAIM_BIG: usize = CLAIM_GROUP * BIG_GROUPS;

/// How the guest splits a list of `n` claims: the whole frames of [`CLAIM_BIG`], then
/// the whole groups of [`CLAIM_GROUP`], then the claims after them. Its own product
/// identity and range checks pin all three, so this only has to agree with them
/// (`list_split` in the guest). A list is never empty here: both claim lists are
/// guarded at the call, since the guest hashes an empty one without a split at all.
pub(super) fn list_split(n: usize) -> Vec<F192> {
    assert!(n > 0, "an empty list has no split");
    let rest = n % CLAIM_BIG;
    vec![
        count(n / CLAIM_BIG),
        count(rest / CLAIM_GROUP),
        count(rest % CLAIM_GROUP),
    ]
}

/// One scheme's declared claims, hashed plainly: 64 bytes a claim, its pubkey then
/// the message it signed, and an empty list hashes the empty string. The guest
/// computes this same digest [`CLAIM_GROUP`] claims at a time (`claim_list_digest`).
pub(super) fn claim_list_digest(claims: impl IntoIterator<Item = [F192; 4]>) -> [F192; 2] {
    hash_to_scalars(&primitives::hash::hash(&cell_bytes(claims.into_iter().flatten())))
}

/// A claim as the four cells the guest hashes and its verifier reads: the pubkey's
/// two halves (`to_bytes`), then the message that pubkey signed.
fn claim_cells(pk: &[u8; 32], message: &[u8; 32]) -> [F192; 4] {
    [
        pack_16_bytes(&pk[..16]),
        pack_16_bytes(&pk[16..]),
        pack_16_bytes(&message[..16]),
        pack_16_bytes(&message[16..]),
    ]
}

pub(super) fn lean_xmss_claim_cells((pk, message): &LeanXmssClaim) -> [F192; 4] {
    claim_cells(&pk.to_bytes(), message)
}

pub(super) fn lean_sphincs_claim_cells((pk, message): &LeanSphincsClaim) -> [F192; 4] {
    claim_cells(&pk.to_bytes(), message)
}

/// The statement's fixed header, ahead of the deferred cells: the seed, the two
/// claim-list digests (leanXMSS then leanSPHINCS), and the DA digest ([`verifier::da::digest`]).
/// Fed to the guest as `STMT_HEADER`, which pins where its deferred cells start;
/// the header cells themselves are written out again there, and only the
/// end-to-end statement check holds the two spellings together.
pub(super) const STATEMENT_HEADER: usize = 8;

/// A plain hash over a lane stream, two lanes a 128-bit cell: what the guest gets
/// from `keccak_cells` over the same cells.
fn lane_hash(lanes: impl Iterator<Item = u64>) -> [F192; 2] {
    let bytes: Vec<u8> = lanes.flat_map(u64::to_le_bytes).collect();
    hash_to_scalars(&primitives::hash::hash(&bytes))
}

/// A node's public statement, hashed to the two words the VM publishes. The
/// guest's `statement_digest` computes exactly this, both for itself and when it
/// rebuilds a child's, which is what forces a whole tree onto one bytecode and
/// each child's claims, bound by the two list digests, onto its parent's lists.
///
/// Fixed-length preimage, so a plain hash, with no domain tag of its own: every
/// field has a fixed width, and the header leads with the environment digest, which
/// binds this bytecode and the proof system's version and so already separates the
/// preimage from every other use of the hash here. The header is hashed as the
/// canonical cells it already is (two lanes each, whence the assert, the guest being
/// unable to hash a third), then all three lanes of each deferred cell. Each claim
/// list's digest binds its own length through the hash's padding, so the pair binds
/// both lists whole.
pub(super) fn statement_digest(
    xmss_signers: &[LeanXmssClaim],
    lean_sphincs_signers: &[LeanSphincsClaim],
    da_digest: [u8; 32],
    defer: &DeferredClaim,
) -> [F192; 2] {
    let seed = leanvm_core::cpu::fs_seed(unified_guest());
    let xmss_digest = claim_list_digest(xmss_signers.iter().map(lean_xmss_claim_cells));
    let lean_sphincs_digest = claim_list_digest(lean_sphincs_signers.iter().map(lean_sphincs_claim_cells));
    let da_digest = hash_to_scalars(&da_digest);
    let header: [F192; STATEMENT_HEADER] = [
        seed[0],
        seed[1],
        xmss_digest[0],
        xmss_digest[1],
        lean_sphincs_digest[0],
        lean_sphincs_digest[1],
        da_digest[0],
        da_digest[1],
    ];
    let mut cells = defer.cells();
    if !cells.len().is_multiple_of(2) {
        cells.push(F192::ZERO); // the guest pairs the odd cell with a zero scalar
    }
    let head = header.iter().flat_map(|x| {
        assert_eq!(x.c2, 0, "a header value is a canonical cell");
        [x.c0, x.c1]
    });
    lane_hash(head.chain(cells.iter().flat_map(|x| x.to_limbs())))
}
