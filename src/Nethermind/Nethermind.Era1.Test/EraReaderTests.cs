// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Core.Test.IO;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.Era1.Test;

internal class EraReaderTests
{
    private class PopulatedTestFile : IDisposable
    {
        private TempPath _tmpFile;
        public string FilePath => _tmpFile.Path;
        public List<(Block, TxReceipt[])> AddedContents { get; }

        public static async Task<PopulatedTestFile> Create()
        {
            TempPath tmpFile = TempPath.GetTempFile();
            using EraWriter builder = new(tmpFile.Path, Substitute.For<ISpecProvider>());
            List<(Block, TxReceipt[])> addedContents = [];
            HeaderDecoder headerDecoder = new();

            async Task AddBlock(Block block, TxReceipt[] receipts)
            {
                Hash256 root = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, MainnetSpecProvider.Instance.GetSpec(block.Header), block.ReceiptsRoot);
                block.Header.ReceiptsRoot = root;
                block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
                addedContents.Add((block, receipts));
                await builder.Add(block, receipts);
            }

            await AddBlock(
                Build.A.Block.WithNumber(0)
                    .WithDifficulty(0)
                    .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject,
                [Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject]);

            await AddBlock(
                Build.A.Block.WithNumber(1)
                    .WithDifficulty(0)
                    .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject,
                [Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject]);

            await AddBlock(
                Build.A.Block.WithNumber(2)
                    .WithDifficulty(0)
                    .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject,
                [Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject]);

            await builder.Finalize();

            return new PopulatedTestFile(tmpFile, addedContents);
        }

        private PopulatedTestFile(TempPath tmpFile, List<(Block, TxReceipt[] e)> addedContents)
        {
            _tmpFile = tmpFile;
            AddedContents = addedContents;
        }

        public void Dispose() => _tmpFile.Dispose();
    }

    [Test]
    public async Task ReadAccumulator_DoesNotThrow()
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new(tmpFile.FilePath);
        Assert.That(() => sut.ReadAccumulator(), Throws.Nothing);
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(2UL)]
    public async Task GetBlockByNumber_DifferentNumber_ReturnsBlockWithCorrectNumber(ulong number)
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new(tmpFile.FilePath);
        (Block result, _) = await sut.GetBlockByNumber(number);
        Assert.That(result.Number, Is.EqualTo(number));
    }

    [Test]
    public async Task GetAsyncEnumerator_EnumerateAll_ReadsAllAddedContents()
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new(tmpFile.FilePath);
        List<(Block, TxReceipt[])> reEnumerated = await sut.ToListAsync();
        Assert.That(reEnumerated, Has.Count.EqualTo(tmpFile.AddedContents.Count));

        for (int i = 0; i < tmpFile.AddedContents.Count; i++)
        {
            AssertBlockEquivalent(reEnumerated[i].Item1, tmpFile.AddedContents[i].Item1);
            reEnumerated[i].Item2.AssertEquivalentTo(tmpFile.AddedContents[i].Item2);
        }
    }

    [Test]
    public async Task VerifyAccumulator_CreateBlocks_AccumulatorMatches()
    {
        using AccumulatorCalculator calculator = new();
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();
        foreach ((Block, TxReceipt[]) tmpFileAddedContent in tmpFile.AddedContents)
        {
            calculator.Add(tmpFileAddedContent.Item1.Hash!, tmpFileAddedContent.Item1.TotalDifficulty!.Value);
        }

        ValueHash256 root = calculator.ComputeRoot();
        using EraReader sut = new(tmpFile.FilePath);
        ValueHash256 fileRoot = await sut.VerifyContent(Substitute.For<ISpecProvider>(), Always.Valid, default);
        Assert.That(root, Is.EqualTo(fileRoot));
    }

    private static void AssertBlockEquivalent(Block actual, Block expected) =>
        Assert.That(actual.ToString(Block.Format.Full), Is.EqualTo(expected.ToString(Block.Format.Full)));
}
