// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>One disjoint write batch per independently folded partition.</summary>
public sealed class PbtWriteBatchSet(PbtWriteBatch account, PbtWriteBatch code, PbtWriteBatch storage) : IDisposable
{
    private readonly PbtWriteBatch[] _batches = [account, code, storage];

    public PbtWriteBatch this[PbtPartition partition] => _batches[(int)partition];

    public int Count
    {
        get
        {
            int count = 0;
            foreach (PbtWriteBatch batch in _batches) count += batch.Count;
            return count;
        }
    }

    public void Dispose()
    {
        foreach (PbtWriteBatch batch in _batches) batch.Dispose();
    }
}
