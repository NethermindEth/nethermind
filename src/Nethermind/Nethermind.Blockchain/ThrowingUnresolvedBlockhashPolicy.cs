// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

/// <summary>
/// Default policy: treat an unresolvable in-window hash as an invariant violation and fail loud (canonical processing).
/// </summary>
public sealed class ThrowingUnresolvedBlockhashPolicy : IUnresolvedBlockhashPolicy
{
    public static readonly ThrowingUnresolvedBlockhashPolicy Instance = new();

    public Hash256? Resolve(BlockHeader currentBlock, ulong number) =>
        throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");
}
