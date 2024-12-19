// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
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
            EraWriter builder = new EraWriter(tmpFile.Path, Substitute.For<ISpecProvider>());
            List<(Block, TxReceipt[])> addedContents = new List<(Block, TxReceipt[])>();
            HeaderDecoder headerDecoder = new HeaderDecoder();

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

        public void Dispose()
        {
            _tmpFile.Dispose();
        }
    }

    [Test]
    public async Task ReadAccumulator_DoesNotThrow()
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new EraReader(tmpFile.FilePath);
        Assert.That(() => sut.ReadAccumulator(), Throws.Nothing);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task GetBlockByNumber_DifferentNumber_ReturnsBlockWithCorrectNumber(int number)
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new EraReader(tmpFile.FilePath);
        (Block result, _) = await sut.GetBlockByNumber(number);
        Assert.That(result.Number, Is.EqualTo(number));
    }

    [Test]
    public async Task GetAsyncEnumerator_EnumerateAll_ReadsAllAddedContents()
    {
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();

        using EraReader sut = new EraReader(tmpFile.FilePath);
        List<(Block, TxReceipt[])> reEnumerated = await sut.ToListAsync();
        reEnumerated.Should().BeEquivalentTo(tmpFile.AddedContents);
    }

    [Test]
    public async Task VerifyAccumulator_CreateBlocks_AccumulatorMatches()
    {
        using AccumulatorCalculator calculator = new();
        using PopulatedTestFile tmpFile = await PopulatedTestFile.Create();
        foreach (var tmpFileAddedContent in tmpFile.AddedContents)
        {
            calculator.Add(tmpFileAddedContent.Item1.Hash!, tmpFileAddedContent.Item1.TotalDifficulty!.Value);
        }

        ValueHash256 root = calculator.ComputeRoot();
        using EraReader sut = new EraReader(tmpFile.FilePath);
        ValueHash256 fileRoot = await sut.VerifyContent(Substitute.For<ISpecProvider>(), Always.Valid, default);
        root.Should().BeEquivalentTo(fileRoot);
    }
}
