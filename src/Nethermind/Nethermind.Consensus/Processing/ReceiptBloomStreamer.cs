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
    private static readonly Lock InstanceLock = new();
    private static ReceiptBloomStreamer? _instance;

    private readonly ConcurrentQueue<TxReceipt> _queue = new();
    private readonly SemaphoreSlim _ready = new(0);

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
        _ready.Release();
    }

    private void Run()
    {
        while (true)
        {
            _ready.Wait();
            while (_queue.TryDequeue(out TxReceipt? receipt))
            {
                _ = receipt.Bloom;
            }
        }
    }
}
