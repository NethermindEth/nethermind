// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Tests.Data;

public class KeyTests
{
    private const string Path = nameof(Key.Path);
    private const string Type = nameof(Key.Type);
    private const string AdditionalKey = nameof(Key.StoragePath);

    private static NibblePath Path0 => NibblePath.FromKey(Values.Key0);
    private static NibblePath Path1A => NibblePath.FromKey(Values.Key1A);
    private static NibblePath Path1B => NibblePath.FromKey(Values.Key1B);

    [Test]
    public void Equality_Path()
    {
        const int length = 63;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Key.Account(Path0).Equals(Key.Account(Path0)), Is.True, $"Same {Path} & {Type}");
            Assert.That(Key.Account(Path0).Equals(Key.Account(Path1A)), Is.False, $"{Path} different at first nibble");
            Assert.That(Key.Account(Path1A).Equals(Key.Account(Path1B)), Is.False, $"{Path} different at last nibble");
            Assert.That(Key.Account(Path1A.SliceTo(length)).Equals(Key.Account(Path1B.SliceTo(length))), Is.True,
                $"Truncated {Path} on different nibble should be equal");
        }
    }

    [Test]
    public void Equality_Type() =>
        Assert.That(Key.Account(Path0).Equals(Key.StorageCell(Path0, Keccak.Zero)), Is.False, $"Different {Type}");

    [Test]
    public void Equality_AdditionalKey()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Key.StorageCell(Path0, Values.Key1A).Equals(Key.StorageCell(Path0, Values.Key1A)), Is.True,
                $"Same {Path} & {Type} & {AdditionalKey}");
            Assert.That(Key.StorageCell(Path0, Values.Key1A).Equals(Key.StorageCell(Path0, Values.Key1B)), Is.False,
                $"Same {Path} & {Type} but different {AdditionalKey}");
        }
    }

    [Test]
    public void Serialization_Account() => ReadWriteAssert(Key.Account(NibblePath.Empty));

    [Test]
    public void Serialization_Storage() => ReadWriteAssert(Key.StorageCell(Path0, Values.Key1A));

    [Test]
    public void LongHash()
    {
        Keccak path0 = default;

        Keccak path1 = default;
        path1.BytesAsSpan[31] = 0x01;

        Keccak path2 = default;
        path2.BytesAsSpan[31] = 0x02;

        Dictionary<ulong, string> hashes = [];

        // account
        Keccak account = Keccak.EmptyTreeHash;
        NibblePath aPath = NibblePath.FromKey(account);

        Unique(Key.Account(account));

        // storage
        Unique(Key.StorageCell(aPath, path0));
        Unique(Key.StorageCell(aPath, path1));
        Unique(Key.StorageCell(aPath, path2));

        // Merkle, all the account prefixes
        for (int i = 0; i < 32; i++)
        {
            Unique(Key.Merkle(aPath.SliceTo(i)));
        }

        // Merkle, branches of 000 addresses
        NibblePath zero = NibblePath.FromKey(Keccak.Zero);

        // Start from 1st as the root is covered above
        for (int i = 1; i < 32; i++)
        {
            Unique(Key.Merkle(zero.SliceTo(i)));
        }

        // Merkle, all the storage prefixes
        for (int i = 0; i < 32; i++)
        {
            Unique(Key.Raw(aPath, DataType.Merkle,
                NibblePath.FromKey(Keccak.EmptyTreeHash).SliceTo(i)));
        }

        for (int x = 0; x < 16; x++)
        {
            NibblePath nibbles = NibblePath.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde" + x.ToString("x").ToLowerInvariant());
            Unique(Key.Raw(aPath, DataType.Merkle, nibbles));

            nibbles = NibblePath.Parse(x.ToString("x").ToLowerInvariant() + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde");
            for (int i = 1; i < 32; i++)
            {
                Unique(Key.Raw(aPath, DataType.Merkle, nibbles.SliceTo(i)));
            }
        }

        // Additional colliding ones
        Unique(Key.Merkle(NibblePath.Parse("DAE3")));
        Unique(Key.Merkle(NibblePath.Parse("251C")));

        // Unique(Key.Merkle(NibblePath.Parse(""))); is added above
        // Unique(Key.Merkle(NibblePath.Parse("0")));
        Unique(Key.Merkle(NibblePath.Parse("02")));
        Unique(Key.Merkle(NibblePath.Parse("002")));
        Unique(Key.Merkle(NibblePath.Parse("0002")));

        return;

        void Unique(in Key key)
        {
            string hex = key.ToString();

            ulong hash = key.GetHashCodeULong();
            if (!hashes.TryAdd(hash, hex))
            {
                string existing = hashes[hash];
                if (existing != hex)
                    Assert.Fail($"The hash for {hex} is the same as for {existing}");
            }
        }
    }

    private static void ReadWriteAssert(in Key expected)
    {
        Span<byte> written = expected.WriteTo(stackalloc byte[expected.MaxByteLength]);
        Key.ReadFrom(written, out Key actual);
        Assert.That(actual.Equals(expected), Is.True);
    }
}
