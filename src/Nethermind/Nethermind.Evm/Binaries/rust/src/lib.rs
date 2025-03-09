use p256::ecdsa::{signature::hazmat::PrehashVerifier, Signature, VerifyingKey};
use std::slice;

#[no_mangle]
pub extern "C" fn VerifyBytes(input_ptr: *const u8, input_len: usize) -> bool {
    // Safety: Ensure input_ptr is not null and length is correct
    if input_ptr.is_null() || input_len != 160 {
        return false;
    }

    // Convert raw pointer to Rust slice
    let input = unsafe { slice::from_raw_parts(input_ptr, input_len) };

    let msg = &input[..32];      // Hashed message
    let sig = &input[32..96];    // Signature (r, s)
    let pk = &input[96..160];    // Public key (x, y)

    // Convert public key to uncompressed SEC1 format
    let mut uncompressed_pk = [0u8; 65];
    uncompressed_pk[0] = 0x04;
    uncompressed_pk[1..].copy_from_slice(pk);

    // Parse signature and public key
    let signature = Signature::from_slice(sig).ok();
    let public_key = VerifyingKey::from_sec1_bytes(&uncompressed_pk).ok();

    // Verify the signature
    if let (Some(signature), Some(public_key)) = (signature, public_key) {
        public_key.verify_prehash(msg, &signature).is_ok()
    } else {
        false
    }
}