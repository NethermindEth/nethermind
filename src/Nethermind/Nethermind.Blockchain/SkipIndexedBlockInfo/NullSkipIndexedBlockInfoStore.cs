// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

public sealed class NullSkipIndexedBlockInfoStore : ISkipIndexedBlockInfoStore
{
    public static readonly NullSkipIndexedBlockInfoStore Instance = new();

    private NullSkipIndexedBlockInfoStore() { }

    public UInt256? GetTotalDifficulty(long blockNumber, in ValueHash256 blockHash) => null;

    public ValueHash256? GetAncestorAt(long blockNumber, in ValueHash256 blockHash, long ancestorBlockNumber) => null;
}
