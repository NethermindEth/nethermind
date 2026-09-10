// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if RUST_EVM
namespace Nethermind.Evm.Rust;

internal static unsafe partial class RustEvmNative
{
    /// <summary>On a host the interpreter is the shared library <c>libnm_ffi</c> beside the executable, or where <c>NETHERMIND_RUST_EVM_LIB</c> points.</summary>
    private const string Lib = "nm_ffi";
}
#endif
