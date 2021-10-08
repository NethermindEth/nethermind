# Nethermind Boneh–Lynn–Shacham (BLS) cryptography signature scheme

Cryptography library the implements the ETH 2.0 version of the Boneh–Lynn–Shacham (BLS) signature scheme, for .NET Core.

This is implemented as a System.Security.Cryptography.AsymmetricAlgorithm, and follows the patterns used by other .NET Core cryptography schemes.

Supports converting private keys to public keys, signing and verifying ETH 2.0 messages, aggregating public keys and signatures, and verification of multiple public key/hash pairs against an aggregate signature, including fast aggregate verify.

Also supports signing and verifying of message hashes, including hash with domain from earlier specifications. (Not used by Eth 2.0)

Cross platform for Windows, Linux, and OSX. On Linux and OSX it also requires the GMP library to be installed.

Does not yet support variants or schemes other than that used by ETH 2.0.

Based on the Herumi cryptography library (native), https://github.com/herumi/bls.

## Getting started

### Pre-requisites

* .NET Core 3.0 development tools (SDK)

On Linux and OSX you also need GMP installed (large number library).

Ubuntu:
```
apt install libgmp-dev
```

OSX:
```
brew install gmp
```

### Compile and run tests

To compile the library and then run the unit tests:

```
dotnet test src/Nethermind/Nethermind.Cryptography.Bls.Tests --verbosity normal
```

### BLS

Library implementation from https://github.com/herumi/bls

This has already been compiled and is in the lib folder.

To re-generate (not usually needed), on Windows:

* Install Visual Studio C++ tools
* Get BLS, MCL, and cybozulib_ext projects
* Open x64 Native Tools Command Prompt for VS 2019 command prompt
* We want Eth 2.0 behavior (e.g. G1 (48 byte) as minmal public key) so define BLS_ETH flag: "set CFLAGS=/DBLS_ETH"
* Call "mklib.bat dll" for MCL, then BLS, as per instructions
* (Can also compile and run test projects, as per instructions; NOTE: will use the modified setvar as above)
* Copy the output DLL from "bin" folder to the runtimes folder for Nethermind.Cryptography.Bls

On Linux and OSX:

* In mcl folder, run: make lib/libmcl.a
* In bls folder, run: make BLS_ETH=1 lib/libbls384_256.so
