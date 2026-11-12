# Test format: leanSPHINCS verification

Check a signature of a 32-byte message under a public key.

## Test case format

```yaml
input:
  public_key: Bytes32 -- the public key, PK.seed then PK.root
  message: Bytes32 -- the message
  signature: Bytes7740 -- the signature
output: bool | null -- whether the signature verifies, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

## Condition

Inputs of any other length are rejected.

The message digest picks the FORS instance and its leaves, and the FORS openings give a FORS public key.
A signature verifies when two conditions hold.
Each layer's counter encodes what the layer signs as a codeword, and the top layer's path reaches `PK.root`.

Each case named `tampered` flips one bit in the first value of one field, except `tampered_top_last_sibling`, which flips the last byte of the signature.
The counter is big-endian, so a `tampered` counter changes by `2^24`.

The cases named `zeroed` set the secret of FORS tree 0, or the last sibling of the top layer, to zero.

`wrong_pk_seed` and `wrong_root` flip one bit of `PK.seed` and of `PK.root` in the public key.

The valid cases come from the `sign` cases, and the public keys from the `key_gen` cases.
The `zeroed` cases start from the known answer of leanVM's Rust implementation, the same bytes as `verify_valid_known_answer`.
