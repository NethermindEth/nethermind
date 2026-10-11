// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Evm.State;

/// <summary>Reads contract code ahead of execution and hands each read to the first caller that takes it.</summary>
/// <remarks>
/// A caller that takes code still being read waits for that read instead of reading the code again. Taken code is
/// not kept, and code held for no taker is capped at <c>maxHeldBytes</c>: a block access list can name contracts the
/// block never runs, so beyond the cap prefetches are skipped rather than held. A failed or missing read hands the
/// caller nothing, and the caller reads the code itself.
/// </remarks>
/// <param name="codeDb">The store the code is read from.</param>
/// <param name="codeCache">Code it already holds is served without a read, so is not prefetched.</param>
/// <param name="maxHeldBytes">The most prefetched code held before it is taken.</param>
/// <param name="logManager">Logs reads that fail; execution meets the same error only if it runs the code.</param>
public sealed class CodePrefetcher(
    IWorldStateScopeProvider.ICodeDb codeDb,
    ICodeCache? codeCache = null,
    long maxHeldBytes = CodePrefetcher.DefaultMaxHeldBytes,
    ILogManager? logManager = null)
{
    /// <summary>Enough for 4k contracts of 64 KiB ahead of execution.</summary>
    public const long DefaultMaxHeldBytes = 256L * 1024 * 1024;

    private static readonly TaskCompletionSource<ReadOnlyMemory<byte>> Taken = CreateTaken();

    // Shared by every prefetcher: reads queued by one block's warm-up never occupy the threads that warm accounts and storage.
    private static readonly ConcurrentQueue<(CodePrefetcher Prefetcher, ValueHash256 CodeHash)> Queue = new();
    // Only readers about to sleep register here, so a queued read takes no lock while any reader is awake.
    private static readonly SemaphoreSlim Wake = new(0);
    private static readonly int ReaderCount = Math.Clamp(Environment.ProcessorCount, 4, 32);
    private static int _readersStarted;
    private static int _idleReaders;

    /// <summary>Readers asleep waiting for queued code.</summary>
    internal static int IdleReaders => Volatile.Read(ref _idleReaders);

    /// <summary>The number of background readers once started.</summary>
    internal static int Readers => ReaderCount;

    private readonly ILogger _logger = logManager is null ? default : logManager.GetClassLogger<CodePrefetcher>();

    // Code taken before any prefetch of it, marked by a bit of its hash: a lock-free add, where the dictionary locks.
    // A shared bit only skips a prefetch, and execution reads that code itself.
    private const int TakenBits = 1 << 19;
    private readonly int[] _takenFirst = new int[TakenBits / 32];

    private readonly ConcurrentDictionary<ValueHash256, TaskCompletionSource<ReadOnlyMemory<byte>>> _reads = new();
    private long _heldBytes;
    private volatile bool _stopped;

    /// <summary>Queues the code to be read on a background reader unless it is cached; see <see cref="Prefetch"/>.</summary>
    public void Enqueue(in ValueHash256 codeHash)
    {
        // Ordinary blocks hit the code cache for almost all code, so it is filtered before it costs a reader handoff.
        if (_stopped || codeCache?.Get(in codeHash) is not null) return;
        StartReaders();
        Queue.Enqueue((this, codeHash));
        WakeIdleReader();
    }

    /// <summary>Stops reading ahead and drops the code no one took.</summary>
    /// <remarks>
    /// Returns without waiting for reads in progress, as the end of a block must not wait on the store; such a read
    /// finishes on its own and is not held. Queued code is dropped unread, and a later <see cref="Take"/> finds nothing.
    /// </remarks>
    public void Stop()
    {
        _stopped = true;
        _reads.Clear();
    }

    /// <summary>Reads the code on the calling thread unless it is cached, already read, being read or taken.</summary>
    public void Prefetch(in ValueHash256 codeHash)
    {
        if (_stopped || Volatile.Read(ref _heldBytes) >= maxHeldBytes || IsTakenFirst(in codeHash)
            || codeCache?.Get(in codeHash) is not null) return;

        TaskCompletionSource<ReadOnlyMemory<byte>> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_reads.TryAdd(codeHash, read)) return;

        ReadOnlyMemory<byte> code = ReadOrDefault(in codeHash);
        // Counted before it is published, so a taker never uncounts code not yet counted.
        Interlocked.Add(ref _heldBytes, code.Length);
        read.SetResult(code);
        // A stop that cleared the reads before this one was added would otherwise leave it held.
        if (_stopped) _reads.TryRemove(codeHash, out _);
    }

    /// <summary>Takes the prefetched code, waiting for a read in flight.</summary>
    /// <returns>The code, or <c>default</c> when it was not prefetched, was taken before, or could not be read.</returns>
    /// <remarks>
    /// Code not prefetched is marked taken, so a later prefetch does not read it again; a prefetch already past that
    /// check can still read it once, and it is dropped at <see cref="Stop"/>.
    /// </remarks>
    public ReadOnlyMemory<byte> Take(in ValueHash256 codeHash)
    {
        if (!_reads.TryGetValue(codeHash, out TaskCompletionSource<ReadOnlyMemory<byte>>? read))
        {
            MarkTakenFirst(in codeHash);
            return default;
        }

        if (ReferenceEquals(read, Taken)) return default;

        ReadOnlyMemory<byte> code = read.Task.GetAwaiter().GetResult();
        if (_reads.TryUpdate(codeHash, Taken, read)) Interlocked.Add(ref _heldBytes, -code.Length);
        return code;
    }

    private static int TakenBit(in ValueHash256 codeHash) =>
        (int)(MemoryMarshal.Read<uint>(codeHash.Bytes[16..]) & (TakenBits - 1));

    private void MarkTakenFirst(in ValueHash256 codeHash)
    {
        int bit = TakenBit(in codeHash);
        Interlocked.Or(ref _takenFirst[bit >> 5], 1 << bit);
    }

    private bool IsTakenFirst(in ValueHash256 codeHash)
    {
        int bit = TakenBit(in codeHash);
        return (Volatile.Read(ref _takenFirst[bit >> 5]) & (1 << bit)) != 0;
    }

    private ReadOnlyMemory<byte> ReadOrDefault(in ValueHash256 codeHash)
    {
        try
        {
            return codeDb.GetCode(in codeHash);
        }
        catch (Exception e)
        {
            // The caller reads the code itself and meets the error there; code no one runs is never read again.
            if (_logger.IsDebug) _logger.Debug($"Code {codeHash} could not be read ahead: {e.Message}");
            return default;
        }
    }

    private static void StartReaders()
    {
        if (Volatile.Read(ref _readersStarted) != 0 || Interlocked.Exchange(ref _readersStarted, 1) != 0) return;

        for (int i = 0; i < ReaderCount; i++)
        {
            new Thread(ReadQueue) { IsBackground = true, Name = "Code prefetch" }.Start();
        }
    }

    /// <remarks>
    /// A reader registers as idle before its last look at the queue, and an enqueue that follows looks for a registered
    /// reader, so one of the two always sees the other: code is never left queued with every reader asleep.
    /// </remarks>
    private static void ReadQueue()
    {
        while (true)
        {
            if (Queue.TryDequeue(out (CodePrefetcher Prefetcher, ValueHash256 CodeHash) item))
            {
                item.Prefetcher.Prefetch(in item.CodeHash);
                continue;
            }

            Interlocked.Increment(ref _idleReaders);
            if (Queue.TryDequeue(out item))
            {
                // Withdraws the registration; one an enqueue claimed first leaves a wake-up, taken after the read so the
                // count stays balanced and no reader sleeps holding code.
                bool claimed = ClaimIdleReader();
                item.Prefetcher.Prefetch(in item.CodeHash);
                if (!claimed) Wake.Wait();
                continue;
            }

            Wake.Wait();
        }
    }

    private static void WakeIdleReader()
    {
        // The enqueue publishes the item with a plain or release store, which a later load may pass on x64 and ARM64.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _idleReaders) != 0 && ClaimIdleReader()) Wake.Release();
    }

    private static bool ClaimIdleReader()
    {
        int idle = Volatile.Read(ref _idleReaders);
        while (idle > 0)
        {
            int seen = Interlocked.CompareExchange(ref _idleReaders, idle - 1, idle);
            if (seen == idle) return true;
            idle = seen;
        }

        return false;
    }

    private static TaskCompletionSource<ReadOnlyMemory<byte>> CreateTaken()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        taken.SetResult(default);
        return taken;
    }
}
