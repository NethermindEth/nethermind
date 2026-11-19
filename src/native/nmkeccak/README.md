# nmkeccak

Keccak-256 rate-block absorb for x64 hosts with AVX2, BMI1 and BMI2, loaded by `Nethermind.Core.Crypto.NativeKeccak`.

`xkcp/` holds the files of the [eXtended Keccak Code Package](https://github.com/XKCP/XKCP) (commit `d20c8fcaecc543f816a0721d0dfc13b4cf27c14d`) needed for the `KeccakP-1600` `plain-64bits` (opt64) implementation, unmodified, with their CC0 headers and the XKCP `LICENSE`. The Keccak-p permutations are designed by Guido Bertoni, Joan Daemen, Michaël Peeters and Gilles Van Assche; the implementation is by Gilles Van Assche and Ronny Van Keer. `config.h` replaces the file the XKCP build generates.

`build.sh <output>` builds the shared library with `$CC` (default `gcc`; the Dockerfiles set `gcc-15`). `Nethermind.Core` runs it on Linux x64 when that compiler is on the path; otherwise the library is absent and the C# absorb runs.
