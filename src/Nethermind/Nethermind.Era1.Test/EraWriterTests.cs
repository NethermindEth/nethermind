// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Blockchain.SkipIndexedBlockInfo;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;

namespace Nethermind.Era1.Test;

internal class EraWriterTests
{
    private static ISkipIndexedBlockInfoStore CreatePassThroughSkipIndexedBlockInfoStore()
    {
        ISkipIndexedBlockInfoStore store = Substitute.For<ISkipIndexedBlockInfoStore>();
        store.GetTotalDifficulty(Arg.Any<BlockHeader?>()).Returns(ci =>
        {
            BlockHeader? h = (BlockHeader?)ci[0];
            return h is null ? null : (UInt256?)BlockHeaderBuilder.DefaultDifficulty;
        });
        return store;
    }

    [Test]
    public void Add_TotalDifficultyIsLowerThanBlock_ThrowsException()
    {
        using MemoryStream stream = new();
        ISkipIndexedBlockInfoStore store = Substitute.For<ISkipIndexedBlockInfoStore>();
        store.GetTotalDifficulty(Arg.Any<BlockHeader>()).Returns(UInt256.Zero);
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), store);

        Block block = Build.A.Block.WithNumber(1).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;

        Assert.That(async () => await sut.Add(
            block,
            Array.Empty<TxReceipt>()), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task Add_AddOneBlock()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());
        Block block1 = Build.A.Block.WithNumber(1)
            .TestObject;

        await sut.Add(block1, Array.Empty<TxReceipt>());
    }

    [Test]
    public async Task Add_AddMoreThanMaximumOf8192_Throws()
    {
        using MemoryStream stream = Substitute.For<MemoryStream>();
        stream.CanWrite.Returns(true);
        stream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>()).Returns(Task.CompletedTask);

        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());
        for (int i = 0; i < EraWriter.MaxEra1Size; i++)
        {
            Block block = Build.A.Block.WithNumber(i)
                .TestObject;
            await sut.Add(block, Array.Empty<TxReceipt>());
        }

        Assert.That(
            () => sut.Add(Build.A.Block.WithNumber(0).TestObject, Array.Empty<TxReceipt>()),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Add_FinalizedCalled_ThrowsException()
    {
        using MemoryStream stream = new();
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());
        Block block = Build.A.Block.WithNumber(1)
            .TestObject;

        await sut.Add(block, Array.Empty<TxReceipt>());
        await sut.Finalize();

        Assert.That(async () => await sut.Add(block, Array.Empty<TxReceipt>()), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Finalize_NoBlocksAdded_ThrowsException()
    {
        using MemoryStream stream = new();

        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Finalize_AddOneBlock_WritesAccumulatorEntry()
    {
        using MemoryStream stream = new();
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());
        byte[] buffer = new byte[40];

        Block block = Build.A.Block.WithNumber(1)
            .TestObject;
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

        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore()))
        {
            Block block = Build.A.Block.WithNumber(0)
                .TestObject;
            await sut.Add(block, Array.Empty<TxReceipt>());
            await sut.Finalize();
        }

        using E2StoreReader fileReader = new(tmpFile.Path);
        fileReader.BlockOffset(0).Should().Be(8);
    }

    [Test]
    public void Dispose_Disposed_InnerStreamIsDisposed()
    {
        using MemoryStream stream = new();
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>(), CreatePassThroughSkipIndexedBlockInfoStore());
        sut.Dispose();

        Assert.That(() => stream.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
    }
}
