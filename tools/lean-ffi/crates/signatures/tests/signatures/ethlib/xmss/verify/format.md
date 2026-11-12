# Test format: leanXMSS verification

Check a signature of a 32-byte message under a public key.

## Test case format

```yaml
input:
  public_key: Bytes32 -- the public key
  message: Bytes32 -- the message
  signature: Bytes2404 -- the signature, which carries its leaf index
output: bool | null -- whether the signature verifies, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

## Condition

Inputs of any other length are rejected.

A signature verifies when both layers' digests are codewords, and the top layer's path reaches the root of the public key.

Each case named `tampered` flips one bit in the first value of one field of one layer.

Each case named `zeroed` sets the first message byte, or the last sibling of the top layer, to zero.

The case `verify_next_leaf_index` changes the leaf index of the signature to the next leaf.

The valid cases come from the `sign` cases, and the public keys from the `key_gen` cases.
