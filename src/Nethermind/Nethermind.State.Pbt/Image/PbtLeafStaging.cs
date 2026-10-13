// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Image;

/// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
/// <remarks>
/// Leaves carry no storage roots and PBT never treats one as authoritative, so accounts are staged as their stem leaves.
/// Code chunks are keyed by a hash of their code hash, so no bytecode can be reassembled as its chunks stream past: they
/// are handed to a <see cref="PbtCodeReassembly"/>, which reassembles each bytecode once the code
/// section ends.
/// </remarks>
public static class PbtLeafStaging
{
    /// <summary>Local byte budget for whole code plus its 32-byte chunk encoding; not a deployment-code limit.
    /// Exhaustion is retryable resource unavailability, not invalid state.</summary>
    private const ulong MaxBufferedCodeBytes = 256 * 1024 * 1024;

    /// <returns>The staged accounts and slots.</returns>
    public static (ulong Accounts, ulong Slots) Stage(PbtLeafIngestion.LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
        PbtCodeReassembly codes, CancellationToken cancellationToken)
    {
        ulong accounts = 0;
        ulong slots = 0;
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
                codes.AddChunk((PbtPath)entry.Key, entry.Leaf);
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
            else
            {
                FlushAccount();
                codes.EndChunks();
            }

            slots++;
            PbtVariableTreeKey slotRunKey = SlotRun.RunKey(entry.Key);
            if (runKey != slotRunKey) FlushRun();
            runKey = slotRunKey;
            PackedSlotRun previous = run;
            run = previous.With(SlotRun.IndexOf(entry.Key), entry.Leaf.ToUInt256());
            SlotRun.Return(previous);
        }
        FlushAccount();
        codes.EndChunks();
        FlushRun();
        return (accounts, slots);

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
                    codes.AddCode(account.CodeHash, (uint)codeSize);
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
}
