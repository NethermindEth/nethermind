// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class RootHeaderCheck(IHistoryHeaderSource headers, IDb availableBlocks, MismatchSink sink, ILogger logger) : ViewObserver
{
    public const int PrefetchedBlocks = 16_384;

    private readonly ValueHash256?[] _roots = new ValueHash256?[PrefetchedBlocks];
    private ulong _firstPrefetched;
    private int _prefetched;

    public ulong Compared { get; private set; }

    public override bool ObservesEveryBlock => true;

    public override bool OnBlock(ulong block, in NodeView view)
    {
        ValueHash256? expected = StateRootAt(block);
        if (expected is null)
        {
            sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingHeader, view.Hash, default));
            return false;
        }

        Compared++;
        CheckMarker(block, expected.Value);

        if (view.Hash == expected.Value) return true;

        sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StateRoot, view.Hash, expected.Value));
        if (logger.IsWarn) logger.Warn($"History walk diverged from the header at block {block}; stopping the comparison there.");
        return false;
    }

    private ValueHash256? StateRootAt(ulong block)
    {
        if (_prefetched == 0 || block < _firstPrefetched || block >= _firstPrefetched + (ulong)_prefetched)
        {
            _firstPrefetched = block;
            _prefetched = (int)Math.Min((ulong)PrefetchedBlocks, ulong.MaxValue - block);
            headers.FillStateRoots(block, _roots.AsSpan(0, _prefetched));
        }

        return _roots[block - _firstPrefetched];
    }

    private void CheckMarker(ulong block, in ValueHash256 expected)
    {
        Span<byte> markerKey = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(markerKey, block);
        Span<byte> marker = availableBlocks.GetSpan(markerKey);
        try
        {
            if (marker.Length == Hash256.Size && marker.SequenceEqual(expected.Bytes)) return;

            sink.Add(new HistoryWalkMismatch(
                block, HistoryWalkMismatchKind.CapturedMarker, marker.Length == Hash256.Size ? new ValueHash256(marker) : default, expected));
        }
        finally
        {
            availableBlocks.DangerousReleaseMemory(marker);
        }
    }
}
