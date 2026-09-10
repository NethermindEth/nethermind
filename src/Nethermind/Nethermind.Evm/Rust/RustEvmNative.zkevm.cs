// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if RUST_EVM
namespace Nethermind.Evm.Rust;

internal static unsafe partial class RustEvmNative
{
    /// <summary>On a zkVM guest the interpreter is linked in with the runtime, and bflat binds <c>__Internal</c> to it.</summary>
    private const string Lib = "__Internal";
}
#endif
