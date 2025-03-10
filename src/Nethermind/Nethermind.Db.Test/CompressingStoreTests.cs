// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class CompressingStoreTests
{
    [Test]
    public void Null()
    {
        Context ctx = new();

        ctx.Compressed[Key] = null;

        Assert.That(ctx.Compressed[Key], Is.Null);
        Assert.That(ctx.Wrapped[Key], Is.Null);
    }

    [Test]
    public void Empty()
    {
        Context ctx = new();

        byte[] empty = [];

        ctx.Compressed[Key] = empty;

        Assert.That(empty, Is.EqualTo(ctx.Compressed[Key]).AsCollection);
        Assert.That(empty, Is.EqualTo(ctx.Wrapped[Key]).AsCollection);
    }

    [Test]
    public void Single()
    {
        Context ctx = new();

        byte[] value = { 13 };

        ctx.Compressed[Key] = value;

        Assert.That(value, Is.EqualTo(ctx.Compressed[Key]).AsCollection);
        Assert.That(value, Is.EqualTo(ctx.Wrapped[Key]).AsCollection);
    }

    [Test]
    public void EOA()
    {
        Context ctx = new();

        Rlp encoded = new AccountDecoder().Encode(new(1));
        ctx.Compressed[Key] = encoded.Bytes;

        Assert.That(encoded.Bytes, Is.EqualTo(ctx.Compressed[Key]).AsCollection);
        ctx.Wrapped[Key]!.Length.Should().Be(5);
    }

    [Test]
    public void EOAWithSPan()
    {
        Context ctx = new();

        Rlp encoded = new AccountDecoder().Encode(new(1));
        ctx.Compressed.PutSpan(Key, encoded.Bytes);

        Assert.That(encoded.Bytes, Is.EqualTo(ctx.Compressed[Key]).AsCollection);
        Assert.That(encoded.Bytes, Is.EqualTo(ctx.Compressed.GetSpan(Key).ToArray()).AsCollection);
        ctx.Wrapped[Key]!.Length.Should().Be(5);
    }

    [Test]
    public void Backward_compatible_read()
    {
        Context ctx = new();
        byte[] value = { 1, 2, 34 };

        ctx.Wrapped[Key] = value;

        Assert.That(value, Is.EqualTo(ctx.Compressed[Key]).AsCollection);
    }

    [Test]
    public void Batch()
    {
        Context ctx = new();

        using (IWriteBatch writeBatch = ctx.Compressed.StartWriteBatch())
        {
            writeBatch[Key] = EOABytes;
        }

        Assert.That(EOABytes, Is.EqualTo(ctx.Compressed[Key]).AsCollection);

        ctx.Wrapped[Key]!.Length.Should().Be(5);
    }

    [Test]
    public void TestTuneForwarded()
    {
        Context ctx = new();

        if (ctx.Compressed is not ITunableDb tunable)
        {
            Assert.Fail("Db must me tunable");
            return;
        }

        tunable.Tune(ITunableDb.TuneType.HeavyWrite);

        ctx.Wrapped.WasTunedWith(ITunableDb.TuneType.HeavyWrite).Should().BeTrue();
    }

    private class Context
    {
        public TestMemDb Wrapped { get; }

        public IDb Compressed { get; }

        public Context()
        {
            Wrapped = new TestMemDb();
            Compressed = Wrapped.WithEOACompressed();
        }
    }

    private static readonly byte[] EOABytes = new AccountDecoder().Encode((Account)new(1)).Bytes;

    private static readonly byte[] Key = { 1 };
}
