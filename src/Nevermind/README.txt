This is a managed (.NET) wrapper for Sipa's secp256k1 ultra-fast implementation.

Sipa's work can be found here
	https://github.com/bitcoin/secp256k1

---------------------------------------------------------------------

BREAKING CHANGE to "Verify" method (added normalizeSignatureOnFailure param)
Generally, these are your use cases:
- Verifying old transactions in the blockchain?  Use "True" or NormalizeSignature
- Verifying new transactions?  Use "False"
(Google "BIP 0062")

---------------------------------------------------------------------

This can now be used in AnyCPU mode.  Include "Secp256k1.Proxy" as your
project's reference.  It will include both a .x86 and .x64 build, and
load whichever is required.

---------------------------------------------------------------------

The secp256k1 implementation by Sipa was created for use primarily for bitcoin
applications.  Microsoft's cryptographic tools support secp256r1, but not the
secp256k1 curve.  Therefore, the bouncy castle library seems an attractive
choice to use in a .NET app.  

Attractive, that is, until you verify the blockchain with it.  24 hours of
100% CPU usage on my very-powerful development machine made it only to block
130000 or so.

Sipa's secp256k1 is 100-1000x faster.

---------------------------------------------------------------------

The settings for Secp256k1.NET are by default "MachineX86 (/MACHINE:X86)". For
64 bit projects, switch it in the project properties. For AnyCPU projects,
use the Proxy project.

---------------------------------------------------------------------