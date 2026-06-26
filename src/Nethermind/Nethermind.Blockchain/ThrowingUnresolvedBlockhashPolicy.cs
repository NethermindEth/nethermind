// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

/// <summary>
/// Default policy: an unresolvable in-window hash is an invariant violation, so canonical block processing
/// fails loud rather than silently computing a wrong (zero) BLOCKHASH.
/// </summary>
public sealed class ThrowingUnresolvedBlockhashPolicy : IUnresolvedBlockhashPolicy
{
    public static readonly ThrowingUnresolvedBlockhashPolicy Instance = new();

    public Hash256? Resolve(BlockHeader currentBlock, ulong number) =>
        throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");
}
