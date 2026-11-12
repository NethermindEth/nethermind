# Test format: leanSPHINCS signing

Sign a 32-byte message with the key of a seed `SK.seed`.

## Test case format

```yaml
input:
  seed: Bytes32 -- the secret seed SK.seed
  message: Bytes32 -- the message
output: Bytes7740 | null -- the signature, or null when the input is rejected
```

All bytes are hex strings prefixed with `0x`.

The signature is the randomizer, the FORS openings, then one layer per tree from the bottom up:

```text
    [ randomizer: 16 | FORS: 4224 | bottom: 1156 | middle: 1156 | top: 1188 ]

    FORS  = 24 openings, each [ secret: 16 | 10 path nodes: 160 ]
    layer = [ counter: 4 | 64 chain values: 1024 | h siblings: 16 h ]
```

The counter is big-endian, and `h` is 8 on the bottom and middle layers and 10 on the top one.

## Condition

A seed or a message of any other length is rejected.

Signing is deterministic: the same seed and message give the same signature.
The randomizer is `PRF_msg(SK.prf, PK.seed, M')`, and the signer rebuilds `PK.root`, which the message digest hashes.

These cases pin the signer of the specification, so two implementations of it agree byte for byte.
`sign_known_answer` is the known answer of leanVM's Rust implementation, and the suite checks it against that value.

A signer must never sign more than `2^30` messages with one key.
