// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Anchor information obtained from the consumer's chain, never from an artifact.</summary>
/// <param name="MaxBufferedCodeBytes">Local byte budget for whole code plus its 32-byte chunk encoding;
/// not a deployment-code limit. Exhaustion is retryable resource unavailability, not invalid state.</param>
/// <param name="ActivationTimestamp">The chain specification's binaryTrieTime, or null when it schedules none.
/// An anchor must precede it; there is nothing to precede when it is null.</param>
internal sealed record PbtImageAnchor(string ChainId, Hash256 GenesisHash, BlockHeader Header,
    ulong? ActivationTimestamp, int MaxBufferedCodeBytes);

/// <summary>Local buffering budget exhausted; this does not classify an artifact as invalid.</summary>
internal sealed class PbtImageResourceLimitException(string message) : Exception(message);

/// <summary>Verifies staged EIP-8347 state against the anchor's MPT root through the preimages.</summary>
/// <remarks>
/// The preimages list the anchor's addresses and slot keys in Keccak path order, which is random in the staged state's
/// BLAKE3 key space. So the listed entries are first bucketed in memory by their staged key, and the buckets are
/// swept in ascending order by parallel workers, each sorting its bucket before reading it, which keeps the reads
/// local. What they read is spooled back into Keccak path order, and a single pass over the spool folds the MPT.
/// The staged state is trusted only for what the walk reads: a listed entry the state lacks fails here, while the
/// caller compares the listed counts with the staged ones to refuse state the preimages never name.
/// </remarks>
internal static class PbtImageVerifier
{
    private const string ReadPhase = "PBT verify preimages";
    private const string MptPhase = "PBT verify MPT";

    /// <summary>Buckets the staged key space is cut into, one per 16-bit prefix of the BLAKE3 address hash.</summary>
    private const int BucketCount = 1 << 16;

    /// <summary>Keccak address path, the account/slot tag, then the Keccak slot path.</summary>
    private const int SpoolKeyLength = 65;

    /// <summary>A listed account or slot: where the staged state holds it, and where the MPT places it.</summary>
    /// <param name="StateKey">The account's basic-data key, or the slot's storage key.</param>
    /// <param name="SlotCount">The slots listed for an account; unused for a slot.</param>
    private readonly record struct Job(PbtStorageTreeKey StateKey, Address Address, ValueHash256 AddressHash,
        ValueHash256 SlotHash, uint SlotCount, bool IsSlot);

    /// <param name="bucketBytes">Memory for the bucket tables, shared by all workers.</param>
    /// <param name="bufferBytes">Spool sort buffer per worker.</param>
    /// <param name="workerCount">Bucket reading workers; zero uses the processor count.</param>
    /// <returns>The accounts and slots the preimages list.</returns>
    public static (ulong Accounts, ulong Slots) Verify(Stream preimages, IPbtPersistence.IReader state,
        PbtImageAnchor anchor, string scratchDirectory, long bucketBytes, int bufferBytes, int workerCount, ILogManager logManager,
        CancellationToken cancellationToken = default)
    {
        ValidateAnchor(anchor);
        ILogger logger = logManager.GetClassLogger(typeof(PbtImageVerifier));
        Stopwatch verifying = Stopwatch.StartNew();
        int workers = workerCount > 0 ? workerCount : Environment.ProcessorCount;
        int capacity = (int)Math.Clamp(bucketBytes / Unsafe.SizeOf<Job>(), 1, Array.MaxLength);
        string directory = Path.Combine(scratchDirectory, $"pbt-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using PbtSortedSpool spool = new(directory, bufferBytes, workers, logManager, cancellationToken);
            (ulong accounts, ulong slots) = Spool(preimages, state, spool, capacity, workers, logManager, cancellationToken);

            ValueHash256 mptRoot;
            using (ProgressReporter progress = PbtImageProgress.Start(MptPhase, "acc", accounts, logManager))
            using (PbtSortedSpool.Cursor cursor = spool.Read())
            {
                ulong folded = 0;
                mptRoot = MptRightmostNodeStore.CalculateRoot(Accounts(), MptRightmostNodeStore.DefaultWindowSize, cancellationToken);

                IEnumerable<KeyValuePair<ValueHash256, byte[]>> Accounts()
                {
                    while (cursor.MoveNext())
                    {
                        progress.Update(++folded);
                        ValueHash256 addressHash = new(cursor.Key[..32]);
                        uint slotCount = BinaryPrimitives.ReadUInt32BigEndian(cursor.Value);
                        Account account = DecodeSlim(cursor.Value[sizeof(uint)..]);
                        ValueHash256 storageRoot = MptRightmostNodeStore.CalculateRoot(Storage(), MptRightmostNodeStore.DefaultWindowSize, cancellationToken);
                        yield return new(addressHash, AccountDecoder.Instance.Encode(account.WithChangedStorageRoot(storageRoot.ToHash256())).Bytes);

                        // An account's tag sorts ahead of its slots', so its listed slots are the records right after it.
                        IEnumerable<KeyValuePair<ValueHash256, byte[]>> Storage()
                        {
                            for (uint index = 0; index < slotCount; index++)
                            {
                                cursor.MoveNext();
                                yield return new(new ValueHash256(cursor.Key[33..]), cursor.Value.ToArray());
                            }
                        }
                    }
                }
            }

            if (mptRoot != anchor.Header.StateRoot!.ValueHash256)
                throw new InvalidDataException("Snapshot does not reproduce the anchor MPT root.");
            if (logger.IsInfo)
                logger.Info($"PBT verified {accounts:N0} accounts and {slots:N0} slots against the MPT root {mptRoot} in {verifying.Elapsed:hh\\:mm\\:ss}.");
            return (accounts, slots);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Buckets the listed entries while the workers drain the buckets into the spool, until the preimages end.</summary>
    /// <remarks>
    /// One reader fills the buckets, pausing while <paramref name="capacity"/> entries are queued. The workers sweep the
    /// buckets round robin in ascending order, through one shared pointer, whenever at least 90% of the capacity is
    /// queued, and unconditionally once the reader is done, so each bucket is taken with as many entries as the budget
    /// allows. Workers idle below that mark, which the reader refills without them.
    /// </remarks>
    private static (ulong Accounts, ulong Slots) Spool(Stream preimages, IPbtPersistence.IReader state, PbtSortedSpool spool,
        int capacity, int workers, ILogManager logManager, CancellationToken cancellationToken)
    {
        Bucket[] buckets = new Bucket[BucketCount];
        for (int index = 0; index < BucketCount; index++) buckets[index] = new Bucket();
        int sweepThreshold = Math.Max(1, (int)(capacity * 0.9));
        using SemaphoreSlim space = new(capacity);
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PbtSortedSpool.Writer[] writers = new PbtSortedSpool.Writer[workers];
        Task[] running = new Task[workers];
        ExceptionDispatchInfo? fault = null;
        ulong accounts = 0;
        ulong slots = 0;
        int queued = 0;
        int nextBucket = -1;
        bool readerDone = false;
        try
        {
            for (int index = 0; index < workers; index++) writers[index] = spool.CreateWriter();
            for (int index = 0; index < workers; index++)
            {
                PbtSortedSpool.Writer writer = writers[index];
                // Dedicated threads: a worker blocked on the spool's segment pool must not hold a pool thread
                // that one of the sorts freeing that segment is queued behind.
                running[index] = Task.Factory.StartNew(() => Sweep(writer), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            using ProgressReporter progress = PbtImageProgress.Start(ReadPhase, "acc", 0, logManager);
            progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(ReadPhase, "acc", p)} | {slots,15:N0} slot");
            PbtPreimageReader reader = new(preimages);
            while (reader.ReadAccount(out Address? address, out uint slotCount, abort.Token))
            {
                progress.Update(++accounts);
                ValueHash256 addressKeyHash = PbtKeyDerivation.AddressKeyHash(address!);
                ValueHash256 addressHash = ValueKeccak.Compute(address!.Bytes);
                Queue(new Job((PbtStorageTreeKey)PbtStateKey.Account(addressKeyHash, PbtKeyDerivation.BasicDataLeafKey),
                    address, addressHash, default, slotCount, IsSlot: false));
                for (uint index = 0; index < slotCount; index++)
                {
                    slots++;
                    ValueHash256 slot = reader.ReadSlot(abort.Token);
                    Queue(new Job(PbtStateKey.Storage(address, addressKeyHash, new UInt256(slot.Bytes, isBigEndian: true)),
                        address, addressHash, ValueKeccak.Compute(slot.Bytes), 0, IsSlot: true));
                }
            }
        }
        // A worker failed and aborted the reader; its own exception is thrown below.
        catch (OperationCanceledException) when (Volatile.Read(ref fault) is not null) { }
        catch
        {
            abort.Cancel();
            throw;
        }
        finally
        {
            Volatile.Write(ref readerDone, true);
            // Workers catch their own failures, so the join itself never throws.
            Task.WaitAll(running.Where(static task => task is not null).ToArray(), CancellationToken.None);
            foreach (PbtSortedSpool.Writer? writer in writers) writer?.Dispose();
        }
        fault?.Throw();
        return (accounts, slots);

        void Queue(in Job job)
        {
            space.Wait(abort.Token);
            Bucket bucket = buckets[BucketOf(job.StateKey)];
            lock (bucket) bucket.Jobs.Add(job);
            Interlocked.Increment(ref queued);
        }

        void Sweep(PbtSortedSpool.Writer writer)
        {
            List<Job> batch = [];
            try
            {
                while (true)
                {
                    abort.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref readerDone))
                    {
                        if (Volatile.Read(ref queued) == 0) return;
                    }
                    else if (Volatile.Read(ref queued) < sweepThreshold)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    // The pointer wraps through int overflow, which 2^32 being a multiple of the bucket count keeps in order.
                    Bucket bucket = buckets[(uint)Interlocked.Increment(ref nextBucket) % BucketCount];
                    lock (bucket)
                    {
                        if (bucket.Jobs.Count == 0) continue;
                        (bucket.Jobs, batch) = (batch, bucket.Jobs);
                    }
                    Interlocked.Add(ref queued, -batch.Count);
                    space.Release(batch.Count);
                    Read(batch, state, writer, abort.Token);
                }
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException) Interlocked.CompareExchange(ref fault, ExceptionDispatchInfo.Capture(exception), null);
                abort.Cancel();
            }
        }
    }

    /// <summary>A bucket's queued entries, swapped out whole by the worker that takes them.</summary>
    private sealed class Bucket
    {
        public List<Job> Jobs = [];
    }

    /// <summary>Reads a bucket's entries from the staged state in key order and spools them under their Keccak paths.</summary>
    private static void Read(List<Job> bucket, IPbtPersistence.IReader state, PbtSortedSpool.Writer writer, CancellationToken cancellationToken)
    {
        Span<Job> jobs = CollectionsMarshal.AsSpan(bucket);
        jobs.Sort(StateOrder);
        Span<byte> key = stackalloc byte[SpoolKeyLength];
        PbtStorageTreeKey? runKey = null;
        ISlotRun run = SlotRun.Empty;
        try
        {
            foreach (ref readonly Job job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.AddressHash.Bytes.CopyTo(key);
                if (!job.IsSlot)
                {
                    Account account = state.GetAccount(new ValueHash256(job.StateKey.Bytes[1..33]))
                        ?? throw new InvalidDataException($"Preimages list the account {job.Address} the snapshot lacks.");
                    byte[] rlp = AccountDecoder.Slim.Encode(account).Bytes;
                    byte[] value = new byte[sizeof(uint) + rlp.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(value, job.SlotCount);
                    rlp.CopyTo(value, sizeof(uint));
                    key[32] = 0;
                    key[33..].Clear();
                    writer.Add(key, value);
                    continue;
                }

                PbtStorageTreeKey slotRunKey = SlotRun.RunKey(job.StateKey);
                if (runKey != slotRunKey)
                {
                    SlotRun.Return(run);
                    run = SlotRun.Empty;
                    run = state.GetSlotRun(slotRunKey);
                    runKey = slotRunKey;
                }
                EvmWord word = run.Get(SlotRun.IndexOf(job.StateKey));
                if (EvmWordSlot.IsZero(word))
                    throw new InvalidDataException($"Preimages list a slot of {job.Address} the snapshot lacks.");
                key[32] = 1;
                job.SlotHash.Bytes.CopyTo(key[33..]);
                writer.Add(key, Rlp.Encode(new UInt256(EvmWordSlot.AsReadOnlySpan(in word), isBigEndian: true)).Bytes);
            }
        }
        finally
        {
            SlotRun.Return(run);
        }
        bucket.Clear();
    }

    /// <summary>The 16-bit address hash prefix after the zone byte, which account and storage keys share, offset for a
    /// storage-zone slot by the 16-bit prefix of its suffix hash, wrapping around.</summary>
    /// <remarks>The offset spreads one contract's storage over consecutive buckets, in its suffix order, rather than
    /// piling it into its address's bucket.</remarks>
    private static int BucketOf(in PbtStorageTreeKey stateKey)
    {
        ReadOnlySpan<byte> key = stateKey.Bytes;
        int bucket = BinaryPrimitives.ReadUInt16BigEndian(key[1..]);
        if (key.Length == PbtStorageTreeKey.MaxLength) bucket += BinaryPrimitives.ReadUInt16BigEndian(key[33..]);
        return bucket % BucketCount;
    }

    /// <summary>Address first, the staged storage layout's order, then the rest of the key.</summary>
    private static int StateOrder(Job left, Job right)
    {
        int order = left.StateKey.Bytes[1..33].SequenceCompareTo(right.StateKey.Bytes[1..33]);
        return order != 0 ? order : left.StateKey.CompareTo(right.StateKey);
    }

    private static Account DecodeSlim(ReadOnlySpan<byte> rlp)
    {
        RlpReader reader = new(rlp);
        return AccountDecoder.Slim.Decode(ref reader)!;
    }

    public static void ValidateAnchor(PbtImageAnchor anchor)
    {
        BlockHeader header = anchor.Header;
        if (anchor.ActivationTimestamp is { } activation && header.Timestamp >= activation || header.Hash is null || header.StateRoot is null)
            throw new InvalidDataException("Image requires a pre-activation MPT anchor.");
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
    }
}
