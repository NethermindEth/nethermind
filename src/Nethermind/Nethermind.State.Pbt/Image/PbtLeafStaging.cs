// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
/// <remarks>
/// Leaves carry no storage roots and PBT never treats one as authoritative, so accounts are staged as their stem leaves.
/// Code chunks are keyed by a hash of their code hash, so no bytecode can be reassembled as its chunks stream past: they
/// are staged as leaves, and <see cref="RebuildCodes"/> later reassembles each bytecode from them, walking the staged
/// accounts for the code sizes. Requiring the reassembled code to consume every staged chunk is what stops a stream
/// carrying code no account names.
/// </remarks>
internal static class PbtLeafStaging
{
    /// <summary>Local byte budget for whole code plus its 32-byte chunk encoding; not a deployment-code limit.
    /// Exhaustion is retryable resource unavailability, not invalid state.</summary>
    private const ulong MaxBufferedCodeBytes = 256 * 1024 * 1024;
    private const string CodePhase = "PBT import code";

    /// <returns>The staged accounts and slots, and the code chunks <see cref="RebuildCodes"/> must consume.</returns>
    public static (ulong Accounts, ulong Slots, long CodeChunks) Stage(PbtLeafIngestion.LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
        CancellationToken cancellationToken)
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
                ValueHash256 addressHash = new(key[1..33]);
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
                    case < PbtKeyDerivation.HeaderStorageOffset or >= PbtKeyDerivation.HeaderStorageOffset + PbtSnapshotCodec.HeaderStorageSlots:
                        throw new InvalidDataException("Snapshot holds a leaf at a reserved account sub-index.");
                }
            }
            else FlushAccount();

            slots++;
            PbtVariableTreeKey slotRunKey = SlotRun.RunKey(entry.Key);
            if (runKey != slotRunKey) FlushRun();
            runKey = slotRunKey;
            PackedSlotRun previous = run;
            run = previous.With(SlotRun.IndexOf(entry.Key), EvmWordSlot.FromStripped(entry.Leaf.Bytes));
            SlotRun.Return(previous);
        }
        FlushAccount();
        FlushRun();
        return (accounts, slots, chunkCount);

        void FlushAccount()
        {
            if (stem is not { } addressHash) return;
            if (basicData.Bytes[..4].IndexOfAnyExcept((byte)0) >= 0)
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
                else if ((ulong)codeSize + ((ulong)codeSize + 30) / 31 * 32 > MaxBufferedCodeBytes)
                    throw new PbtImageResourceLimitException("Code staging requires a larger local buffering budget.");
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

    /// <summary>Reassembles the bytecode of every staged contract account from the staged code chunks into the code column.</summary>
    /// <remarks>
    /// Writes are committed in batches small enough to track in memory, and each batch reopens the reader, so a code
    /// hash shared by many accounts is reassembled once whatever the number of distinct codes.
    /// </remarks>
    /// <param name="chunkCount">The code chunks staged, all of which some account's code must consume.</param>
    public static void RebuildCodes(PbtRocksDbPersistence target, long chunkCount, ILogManager logManager, CancellationToken cancellationToken)
    {
        long consumed = 0;
        float walked = 0;
        ulong rebuilt = 0;
        Dictionary<ValueHash256, uint> pending = [];
        using ProgressReporter progress = PbtImageProgress.Start(CodePhase, "code", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(CodePhase, walked, PbtImageProgress.Counted("code", p)));
        using IPbtPersistence.IReader staged = target.CreateReader();
        IPbtPersistence.IReader written = target.CreateReader();
        IPbtPersistence.IWriteBatch batch = target.CreateStagingWriteBatch(WriteFlags.DisableWAL);
        try
        {
            using IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> accounts = staged.EnumerateAccounts();
            while (accounts.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ValueHash256 addressHash, PbtAccount account) = accounts.Current;
                walked = PbtImageProgress.KeyspaceFraction(addressHash);
                if (account.IsDelegation || account.CodeSize == 0) continue;
                ValueHash256 codeHash = account.CodeHash;
                uint size = account.CodeSize;
                uint? seenSize = pending.TryGetValue(codeHash, out uint pendingSize) ? pendingSize : (uint?)written.GetCode(codeHash)?.Code.Length;
                if (seenSize is { } seen)
                {
                    if (seen != size) throw new InvalidDataException("Accounts claim one code hash with different code sizes.");
                    continue;
                }

                consumed += Rebuild(staged, codeHash, (int)size, batch, cancellationToken);
                pending[codeHash] = size;
                progress.Update(++rebuilt);
                if (pending.Count < PbtLeafIngestion.BatchSize) continue;
                batch.Commit();
                batch.Dispose();
                batch = target.CreateStagingWriteBatch(WriteFlags.DisableWAL);
                written.Dispose();
                written = target.CreateReader();
                pending.Clear();
            }
            batch.Commit();
        }
        finally
        {
            batch.Dispose();
            written.Dispose();
        }
        target.Flush();
        if (consumed != chunkCount) throw new InvalidDataException("Snapshot holds code chunks no account's code accounts for.");
    }

    /// <summary>Reassembles <paramref name="size"/> bytes of code from its stored chunks, absent chunks reading as zeros,
    /// then requires the stored chunks to be exactly the code's canonical ones.</summary>
    /// <returns>The stored chunks the code consumed.</returns>
    private static long Rebuild(IPbtPersistence.IReader staged, in ValueHash256 codeHash, int size, IPbtPersistence.IWriteBatch batch, CancellationToken cancellationToken)
    {
        byte[] code = new byte[size];
        int chunkCount = (int)(((long)size + 30) / 31);
        using ArrayPoolList<ValueHash256> leaves = new(chunkCount, chunkCount);
        using ArrayPoolList<bool> stored = new(chunkCount, chunkCount);
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stored[chunk] = staged.TryGetCodeLeaf(PbtStateKey.Code(codeHash, chunk), out ValueHash256 value);
            leaves[chunk] = value;
            if (stored[chunk]) value.Bytes.Slice(1, Math.Min(31, size - chunk * 31)).CopyTo(code.AsSpan(chunk * 31));
        }

        if (ValueKeccak.Compute(code) != codeHash || Eip7702Constants.IsDelegatedCode(code))
            throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
        using RefCountingMemory chunkMemory = PbtKeyDerivation.ChunkifyCode(code);
        ReadOnlySpan<byte> encodedChunks = chunkMemory.GetSpan();
        long consumed = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            ValueHash256 expected = new(encodedChunks.Slice(chunk * 32, 32));
            if (leaves[chunk] != expected || stored[chunk] != (expected != default))
                throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
            if (stored[chunk]) consumed++;
        }
        batch.SetCode(codeHash, new CodeInfo(code));
        return consumed;
    }
}
