// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
/// <remarks>
/// Leaves carry no storage roots and PBT never treats one as authoritative, so accounts are staged as their stem leaves.
/// Code chunks are keyed by a hash of their code hash, so no bytecode can be reassembled as its chunks stream past: they
/// are staged as leaves, and <see cref="RebuildCodes"/> later reassembles each bytecode using the spooled code hashes
/// and sizes. Requiring the reassembled code to consume every staged chunk is what stops a stream
/// carrying code no account names.
/// </remarks>
public static class PbtLeafStaging
{
    /// <summary>Local byte budget for whole code plus its 32-byte chunk encoding; not a deployment-code limit.
    /// Exhaustion is retryable resource unavailability, not invalid state.</summary>
    private const ulong MaxBufferedCodeBytes = 256 * 1024 * 1024;
    private const string CodePhase = "PBT import code";

    /// <returns>The staged accounts and slots, and the code chunks <see cref="RebuildCodes"/> must consume.</returns>
    public static (ulong Accounts, ulong Slots, long CodeChunks) Stage(PbtLeafIngestion.LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
        PbtSortedSpool.Writer codes, CancellationToken cancellationToken)
    {
        ulong accounts = 0;
        ulong slots = 0;
        long chunkCount = 0;
        ValueHash256? stem = null;
        ValueHash256 basicData = default;
        ValueHash256? codeHash = null;
        ValueHash256? delegation = null;
        PbtVariableTreeKey? runKey = null;
        PackedSlotRun run = SlotRun.Empty;

        foreach (RebuildEntry entry in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = entry.Key.Bytes;
            if (key[0] == Eip8297KeyDerivation.CodeZone)
            {
                FlushAccount();
                batch.Next().SetCodeLeaf((PbtPath)entry.Key, entry.Leaf);
                chunkCount++;
                continue;
            }
            if (key[0] == Eip8297KeyDerivation.AccountZone)
            {
                ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(entry.Key);
                if (stem != addressHash)
                {
                    FlushAccount();
                    stem = addressHash;
                }
                switch (key[^1])
                {
                    case PbtKeyDerivation.BasicDataLeafKey:
                        basicData = entry.Leaf;
                        continue;
                    case PbtKeyDerivation.CodeHashLeafKey:
                        codeHash = entry.Leaf;
                        continue;
                    case PbtKeyDerivation.DelegationLeafKey:
                        delegation = entry.Leaf;
                        continue;
                    case < PbtKeyDerivation.HeaderStorageOffset or >= PbtKeyDerivation.HeaderStorageOffset + PbtKeyDerivation.HeaderStorageSlots:
                        throw new InvalidDataException("Snapshot holds a leaf at a reserved account sub-index.");
                }
            }
            else FlushAccount();

            slots++;
            PbtVariableTreeKey slotRunKey = SlotRun.RunKey(entry.Key);
            if (runKey != slotRunKey) FlushRun();
            runKey = slotRunKey;
            PackedSlotRun previous = run;
            run = previous.With(SlotRun.IndexOf(entry.Key), entry.Leaf.ToUInt256());
            SlotRun.Return(previous);
        }
        FlushAccount();
        FlushRun();
        return (accounts, slots, chunkCount);

        void FlushAccount()
        {
            if (stem is not { } addressHash) return;
            if (!PbtKeyDerivation.IsCanonicalBasicData(basicData.Bytes))
                throw new InvalidDataException("Nonzero basic-data version or reserved bytes.");
            int codeSize = (int)PbtKeyDerivation.ReadBasicDataCodeSize(basicData.Bytes);
            PbtKeyDerivation.UnpackBasicData(basicData.Bytes, out ulong nonce, out UInt256 balance);
            if (nonce == 0 && balance.IsZero && codeSize == 0)
                throw new InvalidDataException("Empty account violates EIP-7523.");

            PbtAccount account;
            if (delegation is { } delegated)
            {
                if (codeSize != PbtAccount.DelegationLength || codeHash is not null || delegated.Bytes[PbtAccount.DelegationLength..].IndexOfAnyExcept((byte)0) >= 0 ||
                    !Eip7702Constants.IsDelegatedCode(delegated.Bytes[..PbtAccount.DelegationLength]))
                    throw new InvalidDataException("Invalid delegation header.");
                account = new PbtAccount(basicData, delegated, IsDelegation: true);
                batch.Next().SetCode(account.CodeHash, new CodeInfo(delegated.Bytes[..PbtAccount.DelegationLength].ToArray()));
            }
            else
            {
                account = new PbtAccount(basicData, codeHash ?? throw new InvalidDataException("Account has no code-hash leaf."), IsDelegation: false);
                if (codeSize == 0)
                {
                    if (account.HasCode) throw new InvalidDataException("Codeless account has a nonempty code hash.");
                }
                else
                {
                    if ((ulong)codeSize + (ulong)PbtKeyDerivation.CodeChunkCount(codeSize) * PbtKeyDerivation.CodeChunkSize > MaxBufferedCodeBytes)
                        throw new PbtImageResourceLimitException("Code staging requires a larger local buffering budget.");
                    Span<byte> size = stackalloc byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)codeSize);
                    codes.Add(account.CodeHash.Bytes, size);
                }
            }
            batch.Next().SetAccount(addressHash, account);
            accounts++;
            stem = null;
            basicData = default;
            codeHash = null;
            delegation = null;
        }

        void FlushRun()
        {
            if (runKey is not { } key) return;
            if (key.Length == PbtPath.KeyLength) batch.Next().SetSlotRun((PbtPath)key, run);
            else batch.Next().SetSlotRun((PbtStoragePath)key, run);
            SlotRun.Return(run);
            run = SlotRun.Empty;
            runKey = null;
        }
    }

    /// <summary>Reassembles the spooled codes from staged chunks into the code column.</summary>
    /// <param name="codes">Code hashes and little-endian byte lengths, deduplicated by the spool.</param>
    /// <param name="chunkCount">The code chunks staged, all of which some account's code must consume.</param>
    /// <param name="concurrency">Maximum reconstruction workers; zero uses the processor count.</param>
    public static void RebuildCodes(PbtRocksDbPersistence target, PbtSortedSpool codes, long chunkCount, int concurrency,
        ILogManager logManager, CancellationToken cancellationToken)
    {
        long consumed = 0;
        float walked = 0;
        ulong rebuilt = 0;
        object writeLock = new();
        IPbtPersistence.IWriteBatch? batch = null;
        int writes = 0;
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = concurrency > 0 ? concurrency : Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };
        using ProgressReporter progress = PbtImageProgress.Start(CodePhase, "code", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(CodePhase, walked, PbtImageProgress.Counted("code", p)));
        try
        {
            Parallel.ForEach<(ValueHash256 Hash, uint Size), IPbtPersistence.IReader>(
                Partitioner.Create(SpooledCodes(), EnumerablePartitionerOptions.NoBuffering), options,
                target.CreateReader,
                (entry, loop, reader) =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        (ValueHash256 codeHash, uint size) = entry;
                        (CodeInfo code, long chunks) = Rebuild(reader, codeHash, (int)size, cancellationToken);
                        lock (writeLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            batch ??= target.CreateStagingWriteBatch(WriteFlags.DisableWAL);
                            batch.SetCode(codeHash, code);
                            consumed += chunks;
                            walked = Math.Max(walked, PbtImageProgress.KeyspaceFraction(codeHash));
                            progress.Update(++rebuilt);
                            if (++writes == PbtLeafIngestion.BatchSize) Commit();
                        }
                        return reader;
                    }
                    catch
                    {
                        loop.Stop();
                        throw;
                    }
                },
                static reader => reader.Dispose());
            cancellationToken.ThrowIfCancellationRequested();
            lock (writeLock) Commit();
        }
        catch (AggregateException failures)
        {
            Exception failure = failures.Flatten().InnerExceptions[0];
            foreach (Exception cause in failures.Flatten().InnerExceptions)
            {
                if (cause is OperationCanceledException) continue;
                failure = cause;
                break;
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            // Parallel.ForEach joins workers before returning or throwing, including their reader cleanup.
            batch?.Dispose();
        }
        target.Flush();
        if (consumed != chunkCount) throw new InvalidDataException("Snapshot holds code chunks no account's code accounts for.");

        IEnumerable<(ValueHash256 Hash, uint Size)> SpooledCodes()
        {
            using PbtSortedSpool.Cursor cursor = codes.Read();
            while (cursor.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return (new ValueHash256(cursor.Key), BinaryPrimitives.ReadUInt32LittleEndian(cursor.Value));
            }
        }

        void Commit()
        {
            if (batch is null) return;
            batch.Commit();
            IPbtPersistence.IWriteBatch committed = batch;
            batch = null;
            committed.Dispose();
            writes = 0;
        }
    }

    /// <summary>Reassembles <paramref name="size"/> bytes of code from its stored chunks, absent chunks reading as zeros,
    /// then requires the stored chunks to be exactly the code's canonical ones.</summary>
    /// <returns>The reconstructed bytecode and the stored chunks it consumed.</returns>
    private static (CodeInfo Code, long Chunks) Rebuild(IPbtPersistence.IReader staged, in ValueHash256 codeHash, int size, CancellationToken cancellationToken)
    {
        byte[] code = new byte[size];
        int chunkCount = (int)PbtKeyDerivation.CodeChunkCount(size);
        using ArrayPoolList<ValueHash256> leaves = new(chunkCount, chunkCount);
        using ArrayPoolList<bool> stored = new(chunkCount, chunkCount);
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stored[chunk] = staged.TryGetCodeLeaf(Eip8297KeyDerivation.OverflowCodeKey(codeHash.Bytes, chunk), out ValueHash256 value);
            leaves[chunk] = value;
            if (stored[chunk]) PbtKeyDerivation.CopyChunkCode(value.Bytes, chunk, code);
        }

        if (ValueKeccak.Compute(code) != codeHash || Eip7702Constants.IsDelegatedCode(code))
            throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
        using RefCountingMemory chunkMemory = PbtKeyDerivation.ChunkifyCode(code);
        ReadOnlySpan<byte> encodedChunks = chunkMemory.GetSpan();
        long consumed = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            ValueHash256 expected = new(encodedChunks.Slice(chunk * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
            if (leaves[chunk] != expected || stored[chunk] != (expected != default))
                throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
            if (stored[chunk]) consumed++;
        }
        return (new CodeInfo(code), consumed);
    }
}
