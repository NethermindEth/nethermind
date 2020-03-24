# Cortex Boneh–Lynn–Shacham (BLS) cryptography signature scheme

Cryptography library the implements the ETH 2.0 version of the Boneh–Lynn–Shacham (BLS) signature scheme, for .NET Core.

This is implemented as a System.Security.Cryptography.AsymmetricAlgorithm, and follows the patterns used by other .NET Core cryptography schemes.

Supports converting private keys to public keys, signing and verifying ETH 2.0 message hash with domain, aggregating public keys and signatures, and verification of multiple public key/hash pairs against an aggregate signature.

Cross platform for Windows, Linux, and OSX. On Linux and OSX it also requires the GMP library to be installed.

Does not yet support signing (or verifying) unhashed data, or variants or schemes other than that used by ETH 2.0.

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
dotnet test test/Cortex.Cryptography.Bls.Tests --verbosity normal
```

### Optional requirements

* PowerShell Core, to run build scripts
* .NET Core 2.1 runtime, to run GitVersion during build
* An editor, e.g. VS Code, if you want to contribute

### Compile, test and package

To run tests and then build a release package, with a gitversion based version number:

```
./build.ps1
```

The NuGet package will be created at:

```
src\Cortex.Cryptography.Bls\bin\Release\Cortex.Cryptography.Bls.<ver>.nupkg'.
```

## Development

Pull requests welcome, but will need to align with the project direction to be accepted.

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
* Copy the output DLL from "bin" folder to the library folder for Cortex

On Linux and OSX:

* In mcl folder, run: make lib/libmcl.a
* In bls folder, run: make BLS_ETH=1 lib/libbls384_256.so

## License

Copyright (C) 2019 Demerzel Solutions Limited

This library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License and GNU General Public License for more details.

You should have received a copy of the GNU Lesser General Public License and GNU General Public License along with this library. If not, see <https://www.gnu.org/licenses/>.
