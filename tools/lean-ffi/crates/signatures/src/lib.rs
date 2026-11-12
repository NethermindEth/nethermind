//! Stands in for upstream's `signatures` crate so the vendored `rec_aggregation` builds unchanged against it:
//! upstream's leanXMSS, and NiceTry v2 in place of upstream's leanSPHINCS.

pub use upstream_signatures::lean_xmss;

pub mod lean_sphincs;

/// The length a hash value is cut to.
const N: usize = 16;
