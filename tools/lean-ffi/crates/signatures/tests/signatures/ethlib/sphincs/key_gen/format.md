# Test format: leanSPHINCS key generation

Derive a leanSPHINCS public key from a 32-byte seed `SK.seed`, the whole secret key.

## Test case format

```yaml
input:
  seed: Bytes32 -- the secret seed SK.seed
output: Bytes32 | null -- the public key, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

Two Keccak-256 hashes of `SK.seed` under the scheme's ASCII name give `PK.seed` and `SK.prf`.
`PK.seed` is the first 16 bytes of `H("leanSPHINCS" | SK.seed | 0x00)`, and `SK.prf` is all of `H("leanSPHINCS" | SK.seed | 0x01)`.
Every chain start and FORS secret is `PRF(PK.seed, SK.seed, ADRS)`, and `PK.root` is the root of the top tree.

The public key is `PK.seed`, then `PK.root`:

```text
    [ PK.seed: 16 | PK.root: 16 ]
```

## Condition

A seed of any length other than 32 bytes is rejected.

These cases pin the key derivation of the specification, so two implementations of it agree byte for byte.
`key_gen_known_answer` is the known answer of leanVM's Rust implementation, and the suite checks it against that value.
