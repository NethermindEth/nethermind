// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal readonly record struct ReorgEntry(BlockInfo Before, BlockInfo After, DateTime At)
{
    public override string ToString() => $"#{Before.Number}: {Before.Hash} -> {After.Hash}";
}

internal class ReorgTracker(int trackingWindow = 64, int historySize = 50)
{
    private static readonly StringComparer _hashComparer = StringComparer.OrdinalIgnoreCase;

    private readonly BlockInfo?[] _blocks = new BlockInfo?[trackingWindow];
    private readonly ReorgEntry[] _reorgs = new ReorgEntry[historySize];

    private readonly Lock _lock = new();
    private int _writeIndex;
    private int _count;

    public ReorgEntry? OnNewHead(BlockInfo head)
    {
        int slot = (int)(head.Number % _blocks.Length);
        BlockInfo? previous = _blocks[slot];
        _blocks[slot] = head;

        if (previous?.Number != head.Number || _hashComparer.Equals(previous.Hash, head.Hash))
            return null;

        ReorgEntry entry = new(previous, head, DateTime.UtcNow);
        Store(entry);
        return entry;
    }

    public IReadOnlyList<ReorgEntry> GetReorgs(int? count = null)
    {
        lock (_lock)
        {
            int take = count is null ? _count : Math.Clamp(count.Value, 0, _count);
            int skip = _count - take;

            ReorgEntry[] result = new ReorgEntry[take];

            int start = _count < _reorgs.Length ? 0 : _writeIndex;
            for (int i = 0; i < take; i++)
                result[i] = _reorgs[(start + skip + i) % _reorgs.Length];

            return result;
        }
    }

    private void Store(ReorgEntry entry)
    {
        lock (_lock)
        {
            _reorgs[_writeIndex] = entry;
            _writeIndex = (_writeIndex + 1) % _reorgs.Length;
            if (_count < _reorgs.Length) _count++;
        }
    }
}
