// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using EraWriter = Nethermind.EraE.Archive.EraWriter;

namespace Nethermind.EraE.Test;

internal sealed class TestEraFile : IDisposable
{
    private readonly TempPath _tmpFile;
    public string FilePath => _tmpFile.Path;
    public List<(Block Block, TxReceipt[] Receipts)> Contents { get; } = [];

    private TestEraFile(TempPath tmpFile) => _tmpFile = tmpFile;

    public static async Task<TestEraFile> Create(
        int preMergeCount,
        int postMergeCount,
        ISpecProvider? specProvider = null)
    {
        specProvider ??= MainnetSpecProvider.Instance;
        TempPath tmpFile = TempPath.GetTempFile();
        using EraWriter writer = new(tmpFile.Path, specProvider);
        TestEraFile file = new(tmpFile);
        HeaderDecoder headerDecoder = new();

        long number = 0;
        UInt256 td = BlockHeaderBuilder.DefaultDifficulty;

        for (int i = 0; i < preMergeCount; i++, number++, td += BlockHeaderBuilder.DefaultDifficulty)
        {
            TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
            Block block = Build.A.Block.WithNumber(number).WithTotalDifficulty(td).TestObject;
            block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
            block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
            file.Contents.Add((block, [receipt]));
            await writer.Add(block, [receipt]);
        }

        for (int i = 0; i < postMergeCount; i++, number++)
        {
            TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
            Block block = Build.A.Block.WithNumber(number).WithPostMergeRules().TestObject;
            block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
            block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
            file.Contents.Add((block, [receipt]));
            await writer.Add(block, [receipt]);
        }

        await writer.Finalize();
        return file;
    }

    public void Dispose() => _tmpFile.Dispose();
}
