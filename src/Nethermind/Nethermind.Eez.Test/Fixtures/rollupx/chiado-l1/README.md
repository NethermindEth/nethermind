# Chiado native L1 regression data

- `headers.json`: unchanged `eth_getBlockByNumber` responses from a verified Chiado node. Includes the canonical pre-merge genesis and post-merge blocks23731489/23731531.
- `genesis.json`: unchanged EEZ testnet699169 deployment genesis, SHA256a13b372869cd205ada78b763409584a38cc22d18bf085da1fdbb2a69271fd263.
- `profile.json`: independently computed from the Chiado preset selected by EEZ RollupX c702f90da19cc149890d0a17a4ef6c200dcb6a21. An independent allocation-trie calculation matches the canonical Chiado genesis state root. The profile uses its specified execution-envelope genesis commitment, which differs from the pre-merge AuRa block hash.

The network profile and verification key were computed separately with ABI words and Keccak. A read-only call to the testnet manager at0xf321dbb2e5ad31d3154da951a0257a38a7867ba7 returned the same verification key for proof system0x1fb73b0954026b91d9771a378ccd6b0c71100d4c. These addresses and the attester address are public deployment data. No private signing material is included.
