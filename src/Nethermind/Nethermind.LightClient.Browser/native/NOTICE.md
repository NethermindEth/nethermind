# Browser native sources

The browser build downloads and verifies source archives through
`prepare-native.ps1`; downloaded files and native build artifacts are not
checked into this repository.

- [Supranational blst v0.3.16](https://github.com/supranational/blst/tree/e7f90de551e8df682f3cc99067d204d8b90d27ad),
  Apache-2.0, provides the portable C implementation statically linked into
  `dotnet.wasm`. The upstream archive includes its license.
- [Nethermind BLS bindings 1.1.0](https://github.com/NethermindEth/blst-bindings/tree/77442f26c7ee4bb46b3c12e7120c6325baa0f2e3),
  MIT with upstream Apache-2.0 notices, supplies the same managed types and
  P/Invoke ABI as the desktop package. The preparation script skips its desktop
  dynamic-library resolver on the browser platform; WASM imports resolve to
  the statically linked implementation. Licenses and notices accompany the
  prepared sources.

The consensus processor, SSZ codecs, journal, trie and code-hash verification
are compiled from the repository's shared sources; JavaScript supplies the
worker, provider bridge, transport and persistent storage.

Bitcoin Core libsecp256k1 v0.7.1 is fetched at commit
`1a53f4961f337b4d166c25fce72ef0dc88806618` under its MIT license (`COPYING`).
The matching Nethermind secp256k1 managed bindings are fetched at commit
`cc56c2919db4f47fb7af865901225f8c53be5f03` under their MIT license (`LICENSE`).
The preparation script retains that license and changes only the desktop
dynamic-library resolver registration for the browser platform.

Additional statically linked precompiles use pinned upstream sources prepared by
`prepare-precompiles.ps1`:

- GNU GMP 6.3.0 `mini-gmp`, commit `fb2a20c31bab706024dce27878eed29a298fc69f`,
  LGPL-3.0-or-later or GPL-2.0-or-later.
- MCL v3.05, commit `e8df0f01e939fda21583258ccfc2caa868e7557c`, BSD-3-Clause.
- C-KZG 2.1.8, commit `e125905e5e01186e6ccb7a0ced4845bf7eddbcfe`, Apache-2.0.
- BoringSSL, commit `8b43ff0f72137b31b23d482f0b2344186d797b03`, the permissive
  upstream `LICENSE` terms.

Managed bindings retain the existing package identities and native entry points.
The preparation script retains upstream license files. C-KZG's bundled Ethereum
trusted setup is checked against a pinned SHA256 hash before browser loading.
