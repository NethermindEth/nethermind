// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Repositories;

namespace Nethermind.AuRa.Test;

/// <summary>
/// Read-only chain level store where every level has a main chain block that is not AuRa-finalized,
/// except <paramref name="finalizedLevel"/> when given. Counts <see cref="LoadLevel"/> calls.
/// </summary>
public sealed class CountingChainLevelInfoRepository(ulong? finalizedLevel = null) : IChainLevelInfoRepository
{
    private readonly ChainLevelInfo _notFinalizedLevel = new(true, new BlockInfo(TestItem.KeccakA, UInt256.One));
    private readonly ChainLevelInfo _finalizedLevel = new(true, new BlockInfo(TestItem.KeccakB, UInt256.One) { IsFinalized = true });

    public ulong LoadLevelCalls { get; private set; }

    public ChainLevelInfo? LoadLevel(ulong number)
    {
        LoadLevelCalls++;
        return number == finalizedLevel ? _finalizedLevel : _notFinalizedLevel;
    }

    public void Delete(ulong number, BatchWrite? batch = null) => throw new NotSupportedException();
    public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null) => throw new NotSupportedException();
    public BatchWrite StartBatch() => throw new NotSupportedException();
    public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers) => throw new NotSupportedException();
}
