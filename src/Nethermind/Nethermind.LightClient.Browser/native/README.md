# Browser precompile build

`prepare-precompiles.ps1` verifies pinned source archives and returns the
MSBuild properties consumed by `precompiles.targets`. It prepares matching
managed bindings outside the repository and skips their desktop library
resolvers for browser execution. The native sources are linked into
`dotnet.wasm`; their filenames match the managed P/Invoke module names.

| Precompile | Native implementation | Browser adaptation |
| --- | --- | --- |
| ModExp | GNU GMP 6.3.0 `mini-gmp` | Export names map to the existing GMP bindings. The native and managed `mpz_t` layouts both contain two 32-bit integers and one pointer. |
| BN254 addition, multiplication, pairing | Herumi MCL v3.05 | Portable C++ arithmetic with 384-bit field and 256-bit scalar storage. The public C ABI retains its 64-bit storage blocks and compiled-time value 46 on WASM. |
| KZG point evaluation and cell operations | Ethereum C-KZG 2.1.8 and the shared blst build | The setup loader uses pointer-sized `size_t`; the setup is initialized with no cell precomputation to reduce browser memory. |
| P256 verification | BoringSSL, pinned to the existing managed binding's submodule | Portable C++ implementation with assembly disabled. The matching P1363 verification API is unchanged. |

The KZG setup is the upstream Ethereum ceremony setup. Its SHA256 is checked
before initialization:
`d39b9f2d047cc9dca2de58f264b6a09448ccd34db967881a6713eacacf0f26b7`.
It is bundled with the published browser files; a relay cannot select its
contents. EIP gas rules, canonical input checks, curve checks, and result
encoding continue to run through the shared Nethermind precompile classes.
The GMP port targets the `mpz` operations exercised by the ModExp precompile.

The source preparation retains upstream licenses and notices. GNU GMP's
`mini-gmp` is available under LGPL-3.0-or-later or GPL-2.0-or-later; MCL is
BSD-3-Clause; C-KZG and blst are Apache-2.0; BoringSSL's archive includes its
complete license and inherited notices. The managed bindings retain their
upstream licensing.
