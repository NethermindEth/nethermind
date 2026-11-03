// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Tests;

/// <summary>
/// All methods in here are helpful in testing but are not needed in the core of Paprika as they breach interfaces,
/// assumptions and boundaries. The level of breach is different for different methods, but still, keeping them in one
/// shameful place is easier for both testing and keeping Paprika clean.
/// </summary>
public static class TestExtensions
{
    public static byte[] ToByteArray(this int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    public static void ShouldHaveAccount(this IReadOnlyBatch read, in Keccak key, ReadOnlySpan<byte> expected)
    {
        if (!read.TryGet(Key.Account(key), out ReadOnlySpan<byte> value))
        {
            Assert.Fail($"Account: {key} should exist.");
        }

        Assert.That(value.ToArray(), Is.EqualTo(expected.ToArray()));
    }

    public static void AssertNoAccount(this IReadOnlyBatch read, in Keccak key)
    {
        if (read.TryGet(Key.Account(key), out ReadOnlySpan<byte> account))
        {
            Assert.That(account.IsEmpty, Is.True);
        }
    }

    public static void AssertStorageValue(this IReadOnlyBatch read, in Keccak key, in Keccak storage,
        ReadOnlySpan<byte> expected)
    {
        if (!read.TryGet(Key.StorageCell(NibblePath.FromKey(key), storage), out ReadOnlySpan<byte> value))
        {
            Assert.Fail($"Storage for the account: {key} @ {storage} should exist.");
        }

        Assert.That(value.ToArray(), Is.EqualTo(expected.ToArray()),
            $"Invalid storage value for account number {key} @ {storage}!");
    }

    public static void AssertNoStorageAt(this IReadOnlyBatch read, in Keccak key, in Keccak storage) =>
        Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(key), storage), out _), Is.False);

    public static void SetAccount(this IBatch batch, in Keccak key, ReadOnlySpan<byte> value) =>
        batch.SetRaw(Key.Account(key), value);

    public static void SetStorage(this IBatch batch, in Keccak key, in Keccak storage, ReadOnlySpan<byte> value) =>
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(key), storage), value);

    /// <summary>
    /// The account of three nibbles <c>AB</c> then <paramref name="nibble"/>: a local key of the data page under the state root
    /// page.
    /// </summary>
    public static Key ThreeNibbleAccount(byte nibble)
    {
        byte[] account = [0xAB, (byte)(nibble << NibblePath.NibbleShift)];
        return Key.Account(NibblePath.FromKey(account, 0, 3));
    }

    /// <summary>
    /// Sets the sixteen <see cref="ThreeNibbleAccount"/>s to 250-byte values, which overflow the map of their data page into
    /// a side page.
    /// </summary>
    public static void SetThreeNibbleAccountsOverflowingIntoSidePage(this IBatch batch)
    {
        byte[] large = new byte[250];
        large.AsSpan().Fill(0x2a);
        for (byte nibble = 0; nibble < 16; nibble++)
        {
            batch.SetRaw(ThreeNibbleAccount(nibble), large);
        }
    }

    /// <summary>Applies <paramref name="write"/> in a batch of its own, committed durably.</summary>
    public static async Task Commit(this IDb db, Action<IBatch> write)
    {
        using IBatch batch = db.BeginNextBatch();
        write(batch);
        await batch.Commit(CommitOptions.FlushDataAndRoot);
    }

    /// <summary>Sets <see cref="Values.Key0"/> to <paramref name="value"/> in a batch of its own, committed durably.</summary>
    public static Task SetAccountAndCommit(this IDb db, byte[] value) => db.Commit(batch => batch.SetAccount(Values.Key0, value));

    /// <summary>Reads <see cref="Values.Key0"/>; empty when it is absent.</summary>
    public static byte[] ReadAccount(this IReadOnlyBatch read) =>
        read.TryGet(Key.Account(Values.Key0), out ReadOnlySpan<byte> value) ? value.ToArray() : [];

    /// <summary>Reads <see cref="Values.Key0"/> through a new reader; empty when it is absent.</summary>
    public static byte[] ReadAccount(this IDb db)
    {
        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        return read.ReadAccount();
    }

    public static Keccak NextKeccak(this Random random)
    {
        Keccak keccak = default;
        random.NextBytes(keccak.BytesAsSpan);
        return keccak;
    }

    public static Keccak[] NextKeccaks(this Random random, int count) => [.. Enumerable.Range(0, count).Select(_ => random.NextKeccak())];

    public static byte NextByte(this Random random) => (byte)random.Next(0, byte.MaxValue);
}
