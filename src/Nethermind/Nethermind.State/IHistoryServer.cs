// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

public readonly record struct HistoryServingScope(ValueHash256 KeyRangeStart, ValueHash256 KeyRangeEnd, ulong FloorBlock, ulong WatermarkBlock);

public readonly record struct HistoryRangeEntry(byte[] Key, ulong Block, ReadOnlyMemory<byte> Value);

public readonly record struct ChangesetChunkEntry(ulong Block, uint ChunkIndex, bool IsLastChunkForBlock, ReadOnlyMemory<byte> Payload);

public interface IHistoryServer
{
    const long HardResponseByteLimit = 2_000_000;

    bool CanServe { get; }

    IReadOnlyList<HistoryServingScope> ServedScopes { get; }

    (IOwnedReadOnlyList<HistoryRangeEntry> Entries, byte[]? NextCursor) GetHistoryRangeAtHeight(
        in ValueHash256 startKey,
        in ValueHash256 endKey,
        ulong height,
        byte[]? cursor,
        long byteLimit,
        int maxEntries,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive,
        ulong toBlockInclusive,
        long byteLimit,
        int maxChunks,
        CancellationToken cancellationToken);
}

public sealed class NullHistoryServer : IHistoryServer
{
    public static readonly NullHistoryServer Instance = new();

    private NullHistoryServer() { }

    public bool CanServe => false;

    public IReadOnlyList<HistoryServingScope> ServedScopes => [];

    public (IOwnedReadOnlyList<HistoryRangeEntry> Entries, byte[]? NextCursor) GetHistoryRangeAtHeight(
        in ValueHash256 startKey, in ValueHash256 endKey, ulong height, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken) =>
        (ArrayPoolList<HistoryRangeEntry>.Empty(), null);

    public async IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive, ulong toBlockInclusive, long byteLimit, int maxChunks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
