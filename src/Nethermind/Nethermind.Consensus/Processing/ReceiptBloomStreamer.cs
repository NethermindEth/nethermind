// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// One process-wide thread that computes each receipt's bloom as its transaction ends, so the block's end has only the
/// aggregate bloom and the receipts root left.
/// </summary>
/// <remarks>
/// A bloom it has not reached by then is computed by whoever reads it first: <see cref="TxReceipt.Bloom"/> computes a
/// missing one, and computing it twice gives the same value.
/// </remarks>
internal sealed class ReceiptBloomStreamer
{
    // Blooms are needed only once the block's transactions end, so lagging a millisecond costs nothing, while waking the
    // thread for every receipt would cost the block thread a wake-up each.
    private const int SleepsBeforeParking = 20;

    private static readonly Lock InstanceLock = new();
    private static ReceiptBloomStreamer? _instance;

    private readonly ConcurrentQueue<TxReceipt> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    // 1 while the thread is parked or about to park.
    private int _parked;

    private ReceiptBloomStreamer()
    {
        Thread thread = new(Run) { IsBackground = true, Name = "Receipt blooms" };
        thread.Start();
    }

    public static ReceiptBloomStreamer Instance
    {
        get
        {
            ReceiptBloomStreamer? instance = Volatile.Read(ref _instance);
            if (instance is not null) return instance;
            using (InstanceLock.EnterScope()) return _instance ??= new ReceiptBloomStreamer();
        }
    }

    public void Add(TxReceipt receipt)
    {
        _queue.Enqueue(receipt);
        // Either Park's last look at the queue sees this receipt, or this sees the flag Park set before it.
        if (Volatile.Read(ref _parked) != 0 && Interlocked.Exchange(ref _parked, 0) != 0) _wake.Release();
    }

    private void Run()
    {
        int idleRounds = 0;
        while (true)
        {
            if (_queue.TryDequeue(out TxReceipt? receipt))
            {
                idleRounds = 0;
                _ = receipt.Bloom;
                continue;
            }

            if (++idleRounds <= SleepsBeforeParking) Thread.Sleep(1);
            else
            {
                Park();
                idleRounds = 0;
            }
        }
    }

    private void Park()
    {
        Interlocked.Exchange(ref _parked, 1);
        if (_queue.IsEmpty) _wake.Wait();

        // A spare release only makes a later Park return early.
        Volatile.Write(ref _parked, 0);
    }
}
