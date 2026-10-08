// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Flat;

// PROBE ONLY - not for merge. Counts trie node reads that reach RocksDB, split by phase and thread kind.
public static class TrieCommitProbe
{
    [ThreadStatic] public static bool IsWarmerThread;

    // 0 = execution, 1 = storage/state root write batch, 2 = after the batch (state root, post-verdict commit)
    public static volatile int Phase;

    public static long PendingAtBatch;
    public static long HintedSlots;

    // [phase * 4 + (warmer ? 2 : 0) + (storage ? 0 : 1)]
    private static readonly long[] Reads = new long[12];

    public static int CountReadThen(bool storage, int length)
    {
        CountRead(storage);
        return length;
    }

    public static void CountRead(bool storage)
    {
        int idx = Phase * 4 + (IsWarmerThread ? 2 : 0) + (storage ? 0 : 1);
        Interlocked.Increment(ref Reads[idx]);
    }

    public static string TakeLine()
    {
        Span<long> r = stackalloc long[12];
        for (int i = 0; i < 12; i++) r[i] = Interlocked.Exchange(ref Reads[i], 0);
        long pending = Interlocked.Exchange(ref PendingAtBatch, 0);
        long hinted = Interlocked.Exchange(ref HintedSlots, 0);
        // s=storage t=state; m=non-warmer w=warmer; e=execution b=batch a=after
        return $"pending={pending} hinted={hinted} " +
               $"e_ms={r[0]} e_mt={r[1]} e_ws={r[2]} e_wt={r[3]} " +
               $"b_ms={r[4]} b_mt={r[5]} b_ws={r[6]} b_wt={r[7]} " +
               $"a_ms={r[8]} a_mt={r[9]} a_ws={r[10]} a_wt={r[11]}";
    }
}
