// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Exports a caller-pinned offline preimage-flat generation into unpublished canonical artifacts.</summary>
/// <remarks>The caller owns the reader, code source and outputs and must hold the source exclusively immutable
/// throughout this operation. This helper never opens or writes a source database. Outputs must pass
/// <see cref="PbtImageVerifier"/> before publication: flat metadata alone does not prove the anchor root.</remarks>
internal static class PbtOfflineSource
{
    /// <summary>Keccak address path, the account/slot tag, then the Keccak slot path.</summary>
    private const int PreimageKeyLength = 65;

    private const string ScanPhase = "PBT export scan";

    public static PbtArtifactWriter.PbtArtifactDigests WriteArtifacts(FlatPersistence.IPersistenceReader source,
        IReadOnlyKeyValueStore codeSource, PbtImageAnchor anchor, string scratchDirectory,
        Stream snapshot, Stream preimages, ILogManager logManager,
        int sortBufferBytes = 256 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortBufferBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.IsPreimageMode || source.CurrentState.BlockNumber != anchor.Header.Number ||
            anchor.Header.StateRoot is null || source.CurrentState.StateRoot != anchor.Header.StateRoot.ValueHash256 ||
            anchor.ActivationTimestamp is { } activation && anchor.Header.Timestamp >= activation || anchor.Header.Hash is null)
            throw new InvalidDataException("Offline source does not match the trusted pre-activation anchor.");
        ILogger logger = logManager.GetClassLogger(typeof(PbtOfflineSource));
        Stopwatch exporting = Stopwatch.StartNew();
        string directory = Path.Combine(scratchDirectory, $"pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using PbtSortedSpool leaves = new(directory, sortBufferBytes / 2, logManager, cancellationToken);
            using PbtSortedSpool rawKeys = new(directory, sortBufferBytes / 2, logManager, cancellationToken);
            Span<byte> preimageKey = stackalloc byte[PreimageKeyLength];
            Span<byte> accountValue = stackalloc byte[Address.Size + sizeof(uint)];
            ulong scannedAccounts = 0;
            ulong scannedSlots = 0;
            foreach ((ValueHash256 accountKey, byte[] accountRlp) in ReadAccounts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Address address = new(accountKey.Bytes[..20]);
                RlpReader rlp = new(accountRlp);
                Account account = AccountDecoder.Slim.Decode(ref rlp) ?? throw new InvalidDataException("Invalid source account.");
                CodeInfo? code = null;
                if (account.HasCode)
                {
                    byte[] bytes = codeSource.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException("Missing source code.");
                    if ((long)bytes.Length + ((long)bytes.Length + 30) / 31 * 32 > anchor.MaxBufferedCodeBytes)
                        throw new PbtImageResourceLimitException("Source code requires a larger local buffering budget.");
                    if (Keccak.Compute(bytes) != account.CodeHash) throw new InvalidDataException("Source code hash mismatch.");
                    code = new CodeInfo(bytes);
                }
                foreach ((PbtPath key, ValueHash256 value) in PbtFlatState.AccountLeaves(PbtKeyDerivation.AddressKeyHash(address), account, code))
                    AddLeaf((PbtStorageTreeKey)key, value);
                ValueHash256 addressHash = ValueKeccak.Compute(address.Bytes);
                uint count = 0;
                using (FlatPersistence.IFlatIterator slots = source.CreateStorageIterator(accountKey, default, ValueKeccak.MaxValue))
                {
                    while (slots.MoveNext())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ValueHash256 slot = slots.CurrentKey;
                        EvmWord value = EvmWordSlot.FromStripped(slots.CurrentValue);
                        if (EvmWordSlot.IsZero(value)) throw new InvalidDataException("Source contains a zero storage slot.");
                        AddLeaf(PbtStateKey.Storage(address, new UInt256(slot.Bytes, isBigEndian: true)), new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
                        addressHash.Bytes.CopyTo(preimageKey);
                        preimageKey[32] = 1;
                        ValueKeccak.Compute(slot.Bytes).Bytes.CopyTo(preimageKey[33..]);
                        rawKeys.Add(preimageKey, slot.Bytes);
                        count = checked(count + 1);
                        scannedSlots++;
                    }
                }
                // The zero tag and slot-hash region keep an account ahead of its own slots.
                preimageKey.Clear();
                addressHash.Bytes.CopyTo(preimageKey);
                address.Bytes.CopyTo(accountValue);
                BinaryPrimitives.WriteUInt32BigEndian(accountValue[Address.Size..], count);
                rawKeys.Add(preimageKey, accountValue);
            }
            // The scan reporter lives as long as the walk it reports, so its final line closes the phase.
            IEnumerable<(ValueHash256 Key, byte[] Rlp)> ReadAccounts()
            {
                using ProgressReporter progress = PbtImageProgress.Start(ScanPhase, "acc", 0, logManager);
                progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(ScanPhase, "acc", p)} | {scannedSlots,15:N0} slot");
                using FlatPersistence.IFlatIterator accounts = source.CreateAccountIterator(default, ValueKeccak.MaxValue);
                while (accounts.MoveNext())
                {
                    progress.Update(++scannedAccounts);
                    yield return (accounts.CurrentKey, accounts.CurrentValue.ToArray());
                }
                // The flat iterator's upper bound is exclusive and truncates to twenty bytes.
                ValueHash256 lastKey = default;
                lastKey.BytesAsSpan[..20].Fill(0xff);
                if (source.GetAccount(new Address(lastKey.Bytes[..20])) is { } lastAccount)
                {
                    progress.Update(++scannedAccounts);
                    yield return (lastKey, AccountDecoder.Slim.Encode(lastAccount).Bytes);
                }
            }

            ulong leafCount = 0;
            ValueHash256 root = PbtImageRootCalculator.Calculate(CountLeaves(), cancellationToken);
            PbtArtifactWriter.PbtArtifactDigests digests = PbtArtifactWriter.Write(snapshot, preimages, root, leafCount,
                Leaves("PBT export snapshot", leafCount), Accounts(), cancellationToken);
            if (logger.IsInfo)
                logger.Info($"PBT export wrote {leafCount:N0} leaves for {scannedAccounts:N0} accounts and {scannedSlots:N0} slots in {exporting.Elapsed:hh\\:mm\\:ss}.");
            return digests;

            IEnumerable<RebuildEntry> CountLeaves()
            {
                foreach (RebuildEntry entry in Leaves("PBT export hash", 0))
                {
                    leafCount++;
                    yield return entry;
                }
            }

            // Leaf keys are prefix-free (34 bytes in zone 0/1, 66 in zone 255), so their raw order is total.
            void AddLeaf(in PbtStorageTreeKey key, ValueHash256 value) => leaves.Add(key.Bytes, value.Bytes);

            // The spool is drained once to hash and once to write, so each drain names its own phase.
            IEnumerable<RebuildEntry> Leaves(string phase, ulong total)
            {
                ulong drained = 0;
                using ProgressReporter progress = PbtImageProgress.Start(phase, "leaf", total, logManager);
                using PbtSortedSpool.Cursor cursor = leaves.Read();
                while (cursor.MoveNext())
                {
                    progress.Update(++drained);
                    yield return new RebuildEntry(new PbtStorageTreeKey(cursor.Key), new ValueHash256(cursor.Value));
                }
            }

            IEnumerable<PbtAccountPreimages> Accounts()
            {
                ulong written = 0;
                using ProgressReporter progress = PbtImageProgress.Start("PBT export preimages", "acc", scannedAccounts, logManager);
                using PbtSortedSpool.Cursor cursor = rawKeys.Read();
                while (cursor.MoveNext())
                {
                    progress.Update(++written);
                    if (cursor.Key[32] != 0) throw new InvalidDataException("Orphan source slot.");
                    ValueHash256 accountHash = new(cursor.Key[..32]);
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(cursor.Value[Address.Size..]);
                    yield return new(new Address(cursor.Value[..Address.Size]), count, Slots());
                    IEnumerable<ValueHash256> Slots()
                    {
                        for (uint index = 0; index < count; index++)
                        {
                            if (!cursor.MoveNext() || cursor.Key[32] != 1 || !cursor.Key[..32].SequenceEqual(accountHash.Bytes))
                                throw new InvalidDataException("Source slot count mismatch.");
                            yield return new ValueHash256(cursor.Value);
                        }
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
