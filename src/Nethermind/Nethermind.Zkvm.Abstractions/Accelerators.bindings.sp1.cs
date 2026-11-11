// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    // SP1's SHA-256 and 256-bit modular multiplication precompiles, which the zkVM standards do not cover.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_sha256_extend(ulong* w);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_sha256_compress(ulong* w, ulong* state);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_uint256_mulmod(ulong* x, ulong* y);
}
