// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.IO;

namespace Nethermind.Era1.Test;
internal class EraWriterTests
{
    [Test]
    public void Add_TotalDifficultyIsLowerThanBlock_ThrowsException()
    {
        using MemoryStream stream = new();
        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        block.Header.TotalDifficulty = 0;

        Assert.That(async () => await sut.Add(
            block,
            Array.Empty<TxReceipt>()), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task Add_AddOneBlock()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());
        Block block1 = Build.A.Block.WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;

        await sut.Add(block1, Array.Empty<TxReceipt>());
    }

    [Test]
    public async Task Add_AddMoreThanMaximumOf8192_Throws()
    {
        using MemoryStream stream = Substitute.For<MemoryStream>();
        stream.CanWrite.Returns(true);
        stream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>()).Returns(Task.CompletedTask);

        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());
        for (int i = 0; i < EraWriter.MaxEra1Size; i++)
        {
            Block block = Build.A.Block.WithNumber(i)
                .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, Array.Empty<TxReceipt>());
        }

        Assert.That(
            () => sut.Add(Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject, Array.Empty<TxReceipt>()),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Add_FinalizedCalled_ThrowsException()
    {
        using MemoryStream stream = new();
        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());
        Block block = Build.A.Block.WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;

        await sut.Add(block, Array.Empty<TxReceipt>());
        await sut.Finalize();

        Assert.That(async () => await sut.Add(block, Array.Empty<TxReceipt>()), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Finalize_NoBlocksAdded_ThrowsException()
    {
        using MemoryStream stream = new();

        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Finalize_AddOneBlock_WritesAccumulatorEntry()
    {
        using MemoryStream stream = new();
        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());
        byte[] buffer = new byte[40];

        Block block = Build.A.Block.WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, Array.Empty<TxReceipt>());

        await sut.Finalize();
        stream.Seek(-buffer.Length - 4 * 8, SeekOrigin.End);
        stream.Read(buffer, 0, buffer.Length);

        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(buffer), Is.EqualTo(EntryTypes.Accumulator));
    }

    [Test]
    public async Task Finalize_AddOneBlock_WritesCorrectBlockIndex()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        EraWriter sut = new EraWriter(tmpFile.Path, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, Array.Empty<TxReceipt>());

        await sut.Finalize();

        using E2StoreReader fileReader = new E2StoreReader(tmpFile.Path);
        fileReader.BlockOffset(0).Should().Be(8);
    }

    [Test]
    public void Dispose_Disposed_InnerStreamIsDisposed()
    {
        using MemoryStream stream = new();
        EraWriter sut = new EraWriter(stream, Substitute.For<ISpecProvider>());
        sut.Dispose();

        Assert.That(() => stream.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase("test", 0, "0x0000000000000000000000000000000000000000000000000000000000000000", "test-00000-00000000.era1")]
    [TestCase("goerli", 1, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "goerli-00001-ffffffff.era1")]
    [TestCase("sepolia", 2, "0x1122ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "sepolia-00002-1122ffff.era1")]
    public void Filename_ValidParameters_ReturnsExpected(string network, int epoch, string hash, string expected)
    {
        Assert.That(EraWriter.Filename(network, epoch, new Hash256(hash)), Is.EqualTo(expected));
    }

    [Test]
    public void Filename_NetworkIsNull_ReturnsException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraWriter.Filename(null, 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }

    [Test]
    public void Filename_NetworkIsEmpty_ReturnsException()
    {
        Assert.That(() => EraWriter.Filename("", 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentException);
    }

    [Test]
    public void Filename_EpochIsNegative_ReturnsException()
    {
        Assert.That(() => EraWriter.Filename("test", -1, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Filename_RootIsNull_ReturnsException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraWriter.Filename("test", 0, null), Throws.ArgumentNullException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }
}