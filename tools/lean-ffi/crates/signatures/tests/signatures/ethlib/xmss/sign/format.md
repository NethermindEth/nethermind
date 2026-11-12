# Test format: leanXMSS signing

Sign a 32-byte message at a leaf index with the key of a seed.

## Test case format

```yaml
input:
  seed: Bytes32 -- the secret seed S_0
  leaf_index: uint32 -- the leaf index, below 2^32
  message: Bytes32 -- the message
output: Bytes2404 | null -- the signature, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

The signature is the leaf index, then one layer per tree:

```text
    [ leaf index: 4 | bottom layer: 1200 | top layer: 1200 ]

    layer = [ randomizer: 16 | 58 chain values: 928 | 16 siblings: 256 ]
```

The leaf index is little-endian.

## Condition

A seed or a message of any other length is rejected.

Signing is deterministic: the same seed, leaf index and message give the same signature.

Signing is not consensus-critical.

These cases pin the signer of the specification note, so two implementations of it agree byte for byte.

A signer must never sign two different messages at one leaf index.
