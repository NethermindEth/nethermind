// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>The progress of a snap sync state ranges phase, saved so that a restarted run can resume from it.</summary>
/// <remarks>
/// Taken while no response is being handled, with the requests in flight counted as still to do, so the queues hold
/// every piece of work left: everything below a partition's <see cref="AccountRangeProgress.NextAccountPath"/> is on
/// disk, as is all storage outside <see cref="StorageRangesToRefetch"/>. Anything written after the checkpoint is not
/// accounted for and has to be discarded before the run resumes, because it may come from an older root or be partly
/// lost with the unflushed writes.
/// </remarks>
public sealed class SnapCheckpoint
{
    private const byte FormatVersion = 1;
    private const int HashLength = 32;

    /// <summary>The number of the lowest pivot any range of this sync was requested at.</summary>
    /// <remarks>Healing with block access lists replays from it, so it has to be at or below every root the data came from.</remarks>
    public required ulong AnchorNumber { get; init; }

    /// <summary>The hash of the block at <see cref="AnchorNumber"/>.</summary>
    public required Hash256 AnchorHash { get; init; }

    /// <summary>How far each account range partition got, one entry per partition.</summary>
    public required AccountRangeProgress[] Partitions { get; init; }

    /// <summary>The accounts whose whole storage is still to download.</summary>
    public required PathWithAccount[] StoragesToRetrieve { get; init; }

    /// <summary>The storage range requests still to send, such as the continuation of a large storage or a request dropped in flight at a shutdown.</summary>
    public required StorageRangeProgress[] StorageRanges { get; init; }

    /// <summary>The accounts whose storage did not verify against the root it was requested with, and is downloaded again from its starting hash.</summary>
    public required AccountWithStorageStartingHash[] AccountsToRefresh { get; init; }

    /// <summary>The hashes of the bytecodes still to download.</summary>
    public required ValueHash256[] CodesToRetrieve { get; init; }

    /// <summary>The accounts whose storage healing has to verify, see <c>IStateSyncPivot.UpdatedStorages</c>.</summary>
    public required Hash256[] UpdatedStorages { get; init; }

    /// <summary>Lists, for each queued storage request, the slot range of each account that the resumed run fetches again.</summary>
    /// <remarks>
    /// A request's starting hash applies to its first account and its limit to its last, as in the snap protocol;
    /// the accounts between them are fetched whole.
    /// </remarks>
    public IEnumerable<(ValueHash256 AccountPath, ValueHash256 From, ValueHash256 To)> StorageRangesToRefetch()
    {
        foreach (PathWithAccount account in StoragesToRetrieve)
        {
            yield return (account.Path, ValueKeccak.Zero, ValueKeccak.MaxValue);
        }

        foreach (StorageRangeProgress range in StorageRanges)
        {
            for (int i = 0; i < range.Accounts.Length; i++)
            {
                ValueHash256 from = i == 0 ? range.StartingHash ?? ValueKeccak.Zero : ValueKeccak.Zero;
                ValueHash256 to = i == range.Accounts.Length - 1 ? range.LimitHash ?? ValueKeccak.MaxValue : ValueKeccak.MaxValue;
                yield return (range.Accounts[i].Path, from, to);
            }
        }

        foreach (AccountWithStorageStartingHash refresh in AccountsToRefresh)
        {
            yield return (refresh.PathAndAccount!.Path, refresh.StorageStartingHash, refresh.StorageHashLimit);
        }
    }

    /// <summary>Serializes the checkpoint for <see cref="Decode"/>.</summary>
    /// <remarks>
    /// The record starts with a format version, and <see cref="Decode"/> rejects any other version, so a change to the
    /// layout has to bump it: a checkpoint written by another version is then discarded and the sync starts over.
    /// </remarks>
    public byte[] Encode()
    {
        ArrayBufferWriter<byte> writer = new();
        writer.Write([FormatVersion]);
        WriteUInt64(writer, AnchorNumber);
        writer.Write(AnchorHash.Bytes);

        WriteCount(writer, Partitions.Length);
        foreach (AccountRangeProgress partition in Partitions)
        {
            writer.Write(partition.NextAccountPath.Bytes);
            writer.Write(partition.AccountPathLimit.Bytes);
            writer.Write([partition.MoreAccountsToRight ? (byte)1 : (byte)0]);
        }

        WriteCount(writer, StoragesToRetrieve.Length);
        foreach (PathWithAccount account in StoragesToRetrieve)
        {
            WriteAccount(writer, account);
        }

        WriteCount(writer, StorageRanges.Length);
        foreach (StorageRangeProgress range in StorageRanges)
        {
            WriteCount(writer, range.Accounts.Length);
            foreach (PathWithAccount account in range.Accounts)
            {
                WriteAccount(writer, account);
            }

            WriteOptionalHash(writer, range.StartingHash);
            WriteOptionalHash(writer, range.LimitHash);
        }

        WriteCount(writer, AccountsToRefresh.Length);
        foreach (AccountWithStorageStartingHash refresh in AccountsToRefresh)
        {
            WriteAccount(writer, refresh.PathAndAccount!);
            writer.Write(refresh.StorageStartingHash.Bytes);
            writer.Write(refresh.StorageHashLimit.Bytes);
        }

        WriteCount(writer, CodesToRetrieve.Length);
        foreach (ValueHash256 code in CodesToRetrieve)
        {
            writer.Write(code.Bytes);
        }

        WriteCount(writer, UpdatedStorages.Length);
        foreach (Hash256 address in UpdatedStorages)
        {
            writer.Write(address.Bytes);
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <exception cref="InvalidDataException">The data is not a checkpoint in the current format.</exception>
    public static SnapCheckpoint Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            Reader reader = new(data);
            byte version = reader.ReadByte();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported snap checkpoint format version {version}.");

            ulong anchorNumber = reader.ReadUInt64();
            Hash256 anchorHash = new(reader.ReadHash());

            AccountRangeProgress[] partitions = new AccountRangeProgress[reader.ReadCount(2 * HashLength + 1)];
            for (int i = 0; i < partitions.Length; i++)
            {
                partitions[i] = new AccountRangeProgress(reader.ReadHash(), reader.ReadHash(), reader.ReadByte() != 0);
            }

            PathWithAccount[] storages = new PathWithAccount[reader.ReadCount(HashLength + sizeof(int))];
            for (int i = 0; i < storages.Length; i++)
            {
                storages[i] = reader.ReadAccount();
            }

            StorageRangeProgress[] ranges = new StorageRangeProgress[reader.ReadCount(sizeof(int) + 2)];
            for (int i = 0; i < ranges.Length; i++)
            {
                PathWithAccount[] accounts = new PathWithAccount[reader.ReadCount(HashLength + sizeof(int))];
                for (int j = 0; j < accounts.Length; j++)
                {
                    accounts[j] = reader.ReadAccount();
                }

                ranges[i] = new StorageRangeProgress(accounts, reader.ReadOptionalHash(), reader.ReadOptionalHash());
            }

            AccountWithStorageStartingHash[] refreshes = new AccountWithStorageStartingHash[reader.ReadCount(3 * HashLength + sizeof(int))];
            for (int i = 0; i < refreshes.Length; i++)
            {
                refreshes[i] = new AccountWithStorageStartingHash
                {
                    PathAndAccount = reader.ReadAccount(),
                    StorageStartingHash = reader.ReadHash(),
                    StorageHashLimit = reader.ReadHash(),
                };
            }

            ValueHash256[] codes = new ValueHash256[reader.ReadCount(HashLength)];
            for (int i = 0; i < codes.Length; i++)
            {
                codes[i] = reader.ReadHash();
            }

            Hash256[] updatedStorages = new Hash256[reader.ReadCount(HashLength)];
            for (int i = 0; i < updatedStorages.Length; i++)
            {
                updatedStorages[i] = new Hash256(reader.ReadHash());
            }

            reader.EnsureEnd();

            return new SnapCheckpoint
            {
                AnchorNumber = anchorNumber,
                AnchorHash = anchorHash,
                Partitions = partitions,
                StoragesToRetrieve = storages,
                StorageRanges = ranges,
                AccountsToRefresh = refreshes,
                CodesToRetrieve = codes,
                UpdatedStorages = updatedStorages,
            };
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or RlpException)
        {
            throw new InvalidDataException("The snap checkpoint is truncated or malformed.", e);
        }
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(writer.GetSpan(sizeof(ulong)), value);
        writer.Advance(sizeof(ulong));
    }

    private static void WriteCount(ArrayBufferWriter<byte> writer, int count)
    {
        BinaryPrimitives.WriteInt32BigEndian(writer.GetSpan(sizeof(int)), count);
        writer.Advance(sizeof(int));
    }

    private static void WriteOptionalHash(ArrayBufferWriter<byte> writer, in ValueHash256? hash)
    {
        writer.Write([hash.HasValue ? (byte)1 : (byte)0]);
        if (hash is { } value) writer.Write(value.Bytes);
    }

    private static void WriteAccount(ArrayBufferWriter<byte> writer, PathWithAccount account)
    {
        writer.Write(account.Path.Bytes);
        byte[] rlp = account.Account is null ? [] : AccountDecoder.Instance.EncodeAsBytes(account.Account);
        WriteCount(writer, rlp.Length);
        writer.Write(rlp);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public byte ReadByte() => ReadBytes(1)[0];

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(sizeof(ulong)));

        public ValueHash256 ReadHash() => new(ReadBytes(HashLength));

        public ValueHash256? ReadOptionalHash()
        {
            // Not a conditional expression: its null would convert to a zero hash through the implicit conversion from Hash256.
            if (ReadByte() == 0) return null;
            return ReadHash();
        }

        /// <summary>Reads an item count, refusing one the remaining data cannot hold so a corrupt count cannot allocate a huge array.</summary>
        public int ReadCount(int minItemLength)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(ReadBytes(sizeof(int)));
            if (count < 0 || (long)count * minItemLength > _data.Length - _position)
                throw new InvalidDataException($"The snap checkpoint holds an invalid item count {count}.");
            return count;
        }

        public PathWithAccount ReadAccount()
        {
            ValueHash256 path = ReadHash();
            ReadOnlySpan<byte> rlp = ReadBytes(ReadCount(1));
            if (rlp.IsEmpty) return new PathWithAccount(path, null);

            RlpReader rlpReader = new(rlp);
            return new PathWithAccount(path, AccountDecoder.Instance.Decode(ref rlpReader));
        }

        public void EnsureEnd()
        {
            if (_position != _data.Length)
                throw new InvalidDataException($"The snap checkpoint has {_data.Length - _position} trailing bytes.");
        }

        private ReadOnlySpan<byte> ReadBytes(int length)
        {
            ReadOnlySpan<byte> bytes = _data.Slice(_position, length);
            _position += length;
            return bytes;
        }
    }
}

/// <summary>How far one account range partition got.</summary>
/// <param name="NextAccountPath">The first account path not yet downloaded.</param>
/// <param name="AccountPathLimit">The last account path of the partition, inclusive. Identifies the partition.</param>
/// <param name="MoreAccountsToRight">Whether any account at or after <paramref name="NextAccountPath"/> is left to download.</param>
public readonly record struct AccountRangeProgress(ValueHash256 NextAccountPath, ValueHash256 AccountPathLimit, bool MoreAccountsToRight);

/// <summary>A queued storage range request.</summary>
/// <param name="Accounts">The accounts the request covers, in path order.</param>
/// <param name="StartingHash">The first slot hash to fetch for the first account, or <c>null</c> for its whole storage.</param>
/// <param name="LimitHash">The last slot hash to fetch for the last account, or <c>null</c> for the rest of its storage.</param>
public readonly record struct StorageRangeProgress(PathWithAccount[] Accounts, ValueHash256? StartingHash, ValueHash256? LimitHash);
