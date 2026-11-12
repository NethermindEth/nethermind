pub mod aggregation;
/// The Keccak hash chain, proven end to end. A `src` module rather than its own
/// test binary so it shares the process with the other workloads.
#[cfg(test)]
mod hash_chain;
#[cfg(any(test, feature = "signers-cache"))]
pub mod signers_cache;

pub use aggregation::{
    AggregateVerifyError, AggregationError, EthereumClaims, EthereumProof, LeanSphincsClaim, GenericClaim, RawGeneric,
    LEANSTARK_DEPS_DOMAIN, MAX_GENERIC_BYTECODE_LOG, MAX_GENERIC_DEPENDENCIES, generic_verification_key,
    generic_witness, verify_generic,
    MAX_RECURSIONS, SnarkProveError, SnarkVerifyError, aggregate, aggregated_vk, deps_hash, deps_public_input_bytes, verify_deps_hash, aggregate_with_stats, warm_up,
};
