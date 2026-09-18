// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
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
    public static void WriteArtifacts(FlatPersistence.IPersistenceReader source, IReadOnlyKeyValueStore codeSource,
        PbtArtifactIdentity identity, PbtImageAnchor anchor, string scratchDirectory,
        Stream snapshot, Stream preimages, Stream manifest, int sortBufferBytes = 256 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortBufferBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.IsPreimageMode || source.CurrentState.BlockNumber != anchor.Header.Number ||
            anchor.Header.StateRoot is null || source.CurrentState.StateRoot != anchor.Header.StateRoot.ValueHash256 ||
            !anchor.IsFinalized || anchor.Header.Timestamp >= anchor.ActivationTimestamp || anchor.Header.Hash is null ||
            identity.ChainId != anchor.ChainId || identity.AnchorNumber != anchor.Header.Number ||
            !string.Equals(identity.AnchorHash, anchor.Header.Hash.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.GenesisHash, anchor.GenesisHash.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.AnchorMptRoot, anchor.Header.StateRoot.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline source does not match the trusted pre-activation anchor.");
        string directory = Path.Combine(scratchDirectory, $"pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using PbtOfflineSourceSort leaves = new(directory, 99, 66, sortBufferBytes / 2, cancellationToken);
            using PbtOfflineSourceSort rawKeys = new(directory, 101, 65, sortBufferBytes / 2, cancellationToken);
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
                foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(PbtKeyDerivation.AddressKeyHash(address), account, code))
                    AddLeaf((PbtTreeKey)key, value);
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
                        byte[] record = new byte[101];
                        addressHash.Bytes.CopyTo(record);
                        record[32] = 1;
                        ValueKeccak.Compute(slot.Bytes).Bytes.CopyTo(record.AsSpan(33));
                        slot.Bytes.CopyTo(record.AsSpan(65));
                        rawKeys.Add(record);
                        count = checked(count + 1);
                    }
                }
                byte[] accountRecord = new byte[101];
                addressHash.Bytes.CopyTo(accountRecord);
                address.Bytes.CopyTo(accountRecord.AsSpan(65));
                BinaryPrimitives.WriteUInt32BigEndian(accountRecord.AsSpan(85), count);
                rawKeys.Add(accountRecord);
            }
            IEnumerable<(ValueHash256 Key, byte[] Rlp)> ReadAccounts()
            {
                using FlatPersistence.IFlatIterator accounts = source.CreateAccountIterator(default, ValueKeccak.MaxValue);
                while (accounts.MoveNext()) yield return (accounts.CurrentKey, accounts.CurrentValue.ToArray());
                // The flat iterator's upper bound is exclusive and truncates to twenty bytes.
                ValueHash256 lastKey = default;
                lastKey.BytesAsSpan[..20].Fill(0xff);
                if (source.GetAccount(new Address(lastKey.Bytes[..20])) is { } lastAccount)
                    yield return (lastKey, AccountDecoder.Slim.Encode(lastAccount).Bytes);
            }

            ulong leafCount = 0;
            ValueHash256 root = PbtImageRootCalculator.Calculate(CountLeaves(), cancellationToken);
            PbtArtifactWriter.Write(snapshot, preimages, manifest, identity, root, leafCount, Leaves(), Accounts(), cancellationToken);

            IEnumerable<RebuildEntry> CountLeaves()
            {
                foreach (RebuildEntry entry in Leaves())
                {
                    leafCount++;
                    yield return entry;
                }
            }

            void AddLeaf(in PbtTreeKey key, ValueHash256 value)
            {
                byte[] record = new byte[99];
                key.Bytes.CopyTo(record);
                record[66] = (byte)key.Length;
                value.Bytes.CopyTo(record.AsSpan(67));
                leaves.Add(record);
            }

            IEnumerable<RebuildEntry> Leaves()
            {
                RebuildEntry? previous = null;
                foreach (byte[] record in leaves.Read())
                {
                    RebuildEntry entry = new(new PbtTreeKey(record.AsSpan(0, record[66])), new ValueHash256(record.AsSpan(67)));
                    if (previous is { } prior && prior.Key == entry.Key)
                    {
                        if (prior.Leaf != entry.Leaf) throw new InvalidDataException("Conflicting source leaves.");
                        continue;
                    }
                    previous = entry;
                    yield return entry;
                }
            }

            IEnumerable<PbtAccountPreimages> Accounts()
            {
                using IEnumerator<byte[]> records = rawKeys.Read().GetEnumerator();
                while (records.MoveNext())
                {
                    byte[] accountRecord = records.Current;
                    if (accountRecord[32] != 0) throw new InvalidDataException("Orphan source slot.");
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(accountRecord.AsSpan(85));
                    yield return new(new Address(accountRecord.AsSpan(65, 20)), count, Slots());
                    IEnumerable<ValueHash256> Slots()
                    {
                        for (uint index = 0; index < count; index++)
                        {
                            if (!records.MoveNext() || records.Current[32] != 1 ||
                                !records.Current.AsSpan(0, 32).SequenceEqual(accountRecord.AsSpan(0, 32)))
                                throw new InvalidDataException("Source slot count mismatch.");
                            yield return new ValueHash256(records.Current.AsSpan(65, 32));
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
