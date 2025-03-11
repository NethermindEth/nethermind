use p256::ecdsa::{signature::hazmat::PrehashVerifier, Signature, VerifyingKey};
use std::slice;
use fastcrypto::hash::{Digest, HashFunction};
use fastcrypto::secp256r1::{Secp256r1PublicKey, Secp256r1Signature};
use fastcrypto::traits::{ToFromBytes};
use p256::elliptic_curve::scalar::IsHigh;

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

#[no_mangle]
pub extern "C" fn VerifyBytesFast(input_ptr: *const u8, input_len: usize) -> bool {
    //println!("VerifyBytesFast: Checking signature");

    // Safety: Ensure input_ptr is not null and length is correct
    if input_ptr.is_null() || input_len != 160 {
        //eprintln!("VerifyBytesFast: Invalid length");
        return false;
    }

    // Convert raw pointer to Rust slice
    let input = unsafe { slice::from_raw_parts(input_ptr, input_len) };

    let msg = &input[..32];      // Hashed message
    let sig = &input[32..96];    // Signature (r, s)
    let pk = &input[96..160];    // Public key (x, y)

    // Prepend 0x04 to the public key (uncompressed SEC1 format)
    let mut uncompressed_pk = [0u8; 65];
    uncompressed_pk[0] = 0x04;
    uncompressed_pk[1..].copy_from_slice(pk);

    // Parse public key and signature
    //println!("VerifyBytesFast: Parsing keys and signature");
    let public_key = Secp256r1PublicKey::from_bytes(&uncompressed_pk).ok();


    // Parse the p256 signature
    let p256_signature = Signature::from_slice(sig).ok();
    if p256_signature.is_none() {
        //eprintln!("VerifyBytesFast: Invalid signature");
        return false;
    }
    let p256_signature = p256_signature.unwrap();

    // Normalize `s` if it's high
    let normalized = if p256_signature.s().is_high().into() {
        //println!("VerifyBytesFast: Normalizing signature");
        p256_signature.normalize_s().unwrap()
    } else {
        p256_signature
    };

    // Create the Secp256r1Signature from bytes
    let signature = Secp256r1Signature::from_bytes(normalized.to_bytes().as_slice()).ok();

    // Perform verification
    if let (Some(public_key), Some(signature)) = (public_key, signature) {
        match public_key.verify_with_hash::<NoOpHash<32>>(msg, &signature){
            Ok(_) => {
                //println!("VerifyBytesFast: Signature verification succeeded.");
                true
            }
            Err(e) => {
                //eprintln!("VerifyBytesFast: Signature verification failed: {}", e);
                false
            }
        }
    } else {
        //println!("VerifyBytesFast: Invalid keys or signature format");
        false
    }
}

pub struct NoOpHash<const DIGEST_LENGTH: usize> {}

impl Default for NoOpHash<32> {
    fn default() -> Self {
        todo!()
    }
}

impl HashFunction<32> for NoOpHash<32> {
    fn update<Data: AsRef<[u8]>>(&mut self, _data: Data) {
        todo!()
    }

    fn finalize(self) -> Digest<32> {
        todo!()
    }

    fn digest<Data: AsRef<[u8]>>(data: Data) -> Digest<32> {
        Digest {
            digest: data.as_ref().try_into().expect("Input must be exactly 32 bytes"),
        }
    }
}