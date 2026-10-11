// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Hashing;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public static class PbtRetainedKey
{
    public const byte Metadata = 0;
    public const byte Address = 0x10;
    public const byte Code = 0x20;
    public const byte AccountGroup = 0x30;
    public const byte CodeGroup = 0x31;
    public const byte StorageGroup = 0x32;
    public const byte Ownership = 0xF0;
    public const byte Clear = 0;
    public const byte Account = 1;
    public const byte HeaderRun = 2;
    public const byte StorageRun = 3;

    // The last byte of an entity's descriptor key, and of that key within its chunk keys.
    private const byte DescriptorFlag = 0;
    private const byte ChunkFlag = 1;

    public static ulong BloomHash(ReadOnlySpan<byte> descriptor) => XxHash64.HashToUInt64(descriptor);

    public static byte[] AddressEntity(in ValueHash256 address, byte tag)
    {
        byte[] key = new byte[35];
        key[0] = Address;
        address.Bytes.CopyTo(key.AsSpan(1));
        key[33] = tag;
        return key;
    }

    public static byte[] CodeEntity(in ValueHash256 hash)
    {
        byte[] key = new byte[34];
        key[0] = Code;
        hash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    public static byte[] Run<TKey>(in TKey path) where TKey : struct, IPbtKey<TKey>
    {
        bool header = typeof(TKey) == typeof(PbtPath);
        ReadOnlySpan<byte> bytes = path.Bytes;
        if (bytes.Length != (header ? 34 : 66) || bytes[0] != (header ? Eip8297KeyDerivation.AccountZone : Eip8297KeyDerivation.StorageZone)
            || (bytes[^1] & 15) != 0 || header && bytes[^1] is < 64 or >= 128)
            throw new InvalidDataException("Invalid retained PBT storage run key.");
        byte[] key = new byte[header ? 36 : 68];
        key[0] = Address;
        bytes.Slice(1, 32).CopyTo(key.AsSpan(1));
        key[33] = header ? HeaderRun : StorageRun;
        bytes[33..].CopyTo(key.AsSpan(34));
        return key;
    }

    public static byte[] Group<TPath>(in TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte family = GroupFamily(path);
        byte[] key = new byte[GroupKeyLength(path.BitDepth)];
        key[0] = family;
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1), checked((ushort)path.BitDepth));
        PbtNodePathOperations.CopyTo(path, key.AsSpan(3));
        ValidateDescriptor(key);
        return key;
    }

    public static byte[] Chunk(ReadOnlySpan<byte> descriptor, uint index)
    {
        byte[] key = new byte[descriptor.Length + 4];
        descriptor.CopyTo(key);
        key[descriptor.Length - 1] = ChunkFlag;
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(descriptor.Length), index);
        return key;
    }

    public static byte[] Owner(ushort id)
    {
        byte[] key = [Ownership, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1), id);
        return key;
    }

    /// <summary>Whether <paramref name="key"/> is an entity's descriptor key, as opposed to a metadata, ownership or chunk record.</summary>
    public static bool IsEntity(ReadOnlySpan<byte> key) => key[0] is not Metadata and not Ownership && !IsChunk(key);

    public static bool IsChunk(ReadOnlySpan<byte> key)
    {
        if (key.Length < 2) return false;
        int descriptorLength = key[0] switch
        {
            Address when key.Length >= 34 => key[33] switch
            {
                Clear or Account => 35,
                HeaderRun => 36,
                StorageRun => 68,
                _ => 0,
            },
            Code => 34,
            AccountGroup or CodeGroup or StorageGroup when key.Length >= 3 => GroupKeyLength(BinaryPrimitives.ReadUInt16BigEndian(key[1..])),
            _ => 0,
        };
        return descriptorLength > 0 && key.Length == descriptorLength + sizeof(uint) && key[descriptorLength - 1] == ChunkFlag;
    }

    private static int GroupKeyLength(int bitDepth) => 3 + PbtBitPrefix.ByteCount(bitDepth) + 1;

    public static void ValidateDescriptor(ReadOnlySpan<byte> key)
    {
        if (key.Length < 2 || key[^1] != DescriptorFlag) throw new InvalidDataException("Invalid retained PBT descriptor key.");
        switch (key[0])
        {
            case Address:
                if (key.Length < 35) throw new InvalidDataException("Invalid retained PBT address key.");
                switch (key[33])
                {
                    case Clear or Account when key.Length == 35: return;
                    case HeaderRun when key.Length == 36 && key[34] is >= 64 and < 128 && (key[34] & 15) == 0: return;
                    case StorageRun when key.Length == 68 && (key[66] & 15) == 0: return;
                    default: throw new InvalidDataException("Invalid retained PBT address subtag or run alignment.");
                }
            case Code when key.Length == 34: return;
            case AccountGroup or CodeGroup or StorageGroup:
                PbtStorageNodePath path = DecodeGroup(key);
                if (GroupFamily(path) != key[0]) throw new InvalidDataException("Retained PBT group partition mismatch.");
                return;
            default: throw new InvalidDataException("Unknown retained PBT entity family.");
        }
    }

    private static byte GroupFamily<TPath>(in TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        PbtPartitions.PartitionOfPath(path) switch
        {
            PbtPartition.Code => CodeGroup,
            PbtPartition.Storage => StorageGroup,
            _ => AccountGroup,
        };

    public static PbtStorageNodePath DecodeGroup(ReadOnlySpan<byte> key)
    {
        if (key.Length < 4) throw new InvalidDataException("Invalid retained PBT group key.");
        int depth = BinaryPrimitives.ReadUInt16BigEndian(key[1..]);
        int maxDepth = key[0] == StorageGroup ? PbtStorageNodePath.MaxBitDepth : PbtNodePath.MaxBitDepth;
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(depth) || depth > maxDepth || key.Length != GroupKeyLength(depth))
            throw new InvalidDataException("Invalid retained PBT group depth.");
        ReadOnlySpan<byte> path = key.Slice(3, key.Length - 4);
        if (!PbtNodeCodec.IsCanonicalPath(path, depth, maxDepth)) throw new InvalidDataException("Invalid retained PBT group padding.");
        return PbtStorageNodePath.Create(path, depth);
    }

    public static PbtPath DecodeHeaderRun(ReadOnlySpan<byte> key)
    {
        Span<byte> bytes = stackalloc byte[34];
        bytes[0] = Eip8297KeyDerivation.AccountZone;
        key.Slice(1, 32).CopyTo(bytes[1..]);
        bytes[33] = key[34];
        return new(bytes);
    }

    public static PbtStoragePath DecodeStorageRun(ReadOnlySpan<byte> key)
    {
        Span<byte> bytes = stackalloc byte[66];
        bytes[0] = Eip8297KeyDerivation.StorageZone;
        key.Slice(1, 32).CopyTo(bytes[1..]);
        key.Slice(34, 33).CopyTo(bytes[33..]);
        return new(bytes);
    }
}
