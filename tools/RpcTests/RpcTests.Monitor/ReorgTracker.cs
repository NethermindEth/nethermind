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

    public ReorgEntry? OnNewHead(BlockInfo block)
    {
        int slot = (int)(block.Number % _blocks.Length);
        BlockInfo? prevBlock = _blocks[slot];
        _blocks[slot] = block;

        if (prevBlock is null || prevBlock.Number != block.Number || _hashComparer.Equals(prevBlock.Hash, block.Hash))
            return null;

        ReorgEntry entry = new(prevBlock, block, DateTime.UtcNow);
        Store(entry);
        return entry;
    }

    public IReadOnlyList<ReorgEntry> GetReorgs(DateTime? since = null)
    {
        lock (_lock)
        {
            List<ReorgEntry> result = [];
            for (int i = 0; i < _count; i++)
            {
                int index = (_writeIndex - 1 - i + _reorgs.Length) % _reorgs.Length;
                ReorgEntry entry = _reorgs[index];
                if (since is { } from && entry.At < from)
                    break;
                result.Add(entry);
            }

            result.Reverse();
            return result;
        }
    }

    public IReadOnlyList<ReorgEntry> GetReorgs(TimeSpan? period = null) =>
        GetReorgs(period is not null ? DateTime.UtcNow - period.Value : null);

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
