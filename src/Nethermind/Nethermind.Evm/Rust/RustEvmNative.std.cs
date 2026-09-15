// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if RUST_EVM
namespace Nethermind.Evm.Rust;

internal static unsafe partial class RustEvmNative
{
    /// <summary>On a host the interpreter is the shared library <c>libnm_ffi</c> beside the executable, or where <c>NETHERMIND_RUST_EVM_LIB</c> points.</summary>
    private const string Lib = "nm_ffi";

    /// <summary>
    /// On a host the precompiles run here, on the native libraries this client links (BLS, secp256k1,
    /// KZG); the interpreter's own are written for a zkVM, where the machine replaces them.
    /// </summary>
    public const bool RunPrecompilesHere = true;
}
#endif
