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

public readonly record struct ChangesetChunkEntry(ulong Block, uint ChunkIndex, bool IsLastChunkForBlock, ReadOnlyMemory<byte> Payload);

public enum HistoryRowColumn : byte
{
    AccountHistory,
    StorageHistory,
    StorageClears,
    AvailableBlocks,
    Code,
}

public readonly record struct HistoryRowEntry(byte[] Key, ReadOnlyMemory<byte> Value);

public interface IHistoryServer
{
    const long HardResponseByteLimit = 2_000_000;

    const int MaxRowKeyBytes = 128;

    const int MaxInFlightRequestsPerPeer = 4;

    bool CanServe { get; }

    bool CanServeFullClone { get; }

    byte RowFormatVersion { get; }

    IReadOnlyList<HistoryServingScope> ServedScopes { get; }

    IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive,
        ulong toBlockInclusive,
        long byteLimit,
        int maxChunks,
        CancellationToken cancellationToken);

    (IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused) GetHistoryRows(
        HistoryRowColumn column,
        byte[] startKey,
        byte[] endKey,
        byte[]? cursor,
        long byteLimit,
        int maxEntries,
        CancellationToken cancellationToken);
}

public sealed class NullHistoryServer : IHistoryServer
{
    public static readonly NullHistoryServer Instance = new();

    private NullHistoryServer() { }

    public bool CanServe => false;

    public bool CanServeFullClone => false;

    public byte RowFormatVersion => 0;

    public IReadOnlyList<HistoryServingScope> ServedScopes => [];

    public async IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive, ulong toBlockInclusive, long byteLimit, int maxChunks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public (IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused) GetHistoryRows(
        HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken) =>
        (ArrayPoolList<HistoryRowEntry>.Empty(), null, true);
}
