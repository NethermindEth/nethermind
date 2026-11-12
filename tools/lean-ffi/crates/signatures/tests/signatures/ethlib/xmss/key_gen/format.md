# Test format: leanXMSS key generation

Derive a leanXMSS public key from a 32-byte seed.

## Test case format

```yaml
input:
  seed: Bytes32 -- the secret seed S_0
output: Bytes32 | null -- the public key, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

The public key is the root of the top tree, then the public parameter:

```text
    [ root: 16 | public parameter: 16 ]
```

## Condition

A seed of any length other than 32 bytes is rejected.

Key generation is not consensus-critical.

These cases pin the derivation of the specification note, so two implementations of it agree byte for byte.
