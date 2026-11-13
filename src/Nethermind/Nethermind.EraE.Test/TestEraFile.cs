// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Compression;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Snappier;
using E2StoreReader = Nethermind.EraE.E2Store.E2StoreReader;
using EraWriter = Nethermind.EraE.Archive.EraWriter;

namespace Nethermind.EraE.Test;

internal sealed class TestEraFile : IDisposable
{
    private readonly TempPath _tmpFile;
    public string FilePath => _tmpFile.Path;
    public List<(Block Block, TxReceipt[] Receipts)> Contents { get; } = [];

    private TestEraFile(TempPath tmpFile) => _tmpFile = tmpFile;

    public static async Task<TestEraFile> Create(
        uint preMergeCount,
        uint postMergeCount,
        ISpecProvider? specProvider = null,
        Transaction? transaction = null)
    {
        specProvider ??= MainnetSpecProvider.Instance;
        TempPath tmpFile = TempPath.GetTempFile();
        using EraWriter writer = new(tmpFile.Path, specProvider);
        TestEraFile file = new(tmpFile);
        HeaderDecoder headerDecoder = new();

        ulong number = 0;
        UInt256 td = BlockHeaderBuilder.DefaultDifficulty;

        for (uint i = 0; i < preMergeCount; i++, number++, td += BlockHeaderBuilder.DefaultDifficulty)
        {
            TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
            Block block = Build.A.Block.WithNumber(number).WithTotalDifficulty(td)
                .WithTransactions(transaction is null ? [] : [transaction]).TestObject;
            block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
            block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
            file.Contents.Add((block, [receipt]));
            await writer.Add(block, [receipt]);
        }

        for (uint i = 0; i < postMergeCount; i++, number++)
        {
            TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
            Block block = Build.A.Block.WithNumber(number).WithPostMergeRules()
                .WithTransactions(transaction is null ? [] : [transaction]).TestObject;
            block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
            block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
            file.Contents.Add((block, [receipt]));
            await writer.Add(block, [receipt]);
        }

        await writer.Finalize();
        return file;
    }

    /// <summary>Rewrites the stored header at <paramref name="slot"/> so it decodes with <paramref name="number"/>, leaving the index and all other entries unchanged.</summary>
    public void OverwriteHeaderNumber(ulong slot, ulong number)
    {
        BlockHeader header = Contents[(int)slot].Block.Header.Clone();
        header.Number = number;
        using MemoryStream compressed = new();
        using (SnappyStream compressor = new(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(new HeaderDecoder().Encode(header).Bytes);
        }

        long position;
        using (E2StoreReader reader = new(FilePath))
        {
            position = reader.HeaderOffset(slot);
        }

        using FileStream file = new(FilePath, FileMode.Open, FileAccess.ReadWrite);
        Span<byte> entryHeader = stackalloc byte[8];
        file.Position = position;
        file.ReadExactly(entryHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(entryHeader[2..]) != compressed.Length)
            throw new InvalidOperationException("The rewritten header must compress to the length of the original entry.");

        file.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
    }

    public void Dispose() => _tmpFile.Dispose();
}
