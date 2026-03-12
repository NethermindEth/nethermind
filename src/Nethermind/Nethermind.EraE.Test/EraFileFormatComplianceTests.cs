// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.EraE.Archive;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.EraE.Test;

/// <summary>
/// Verifies that EraWriter produces files whose binary layout matches the EraE spec exactly:
/// TLV structure, entry type codes, section ordering, and ComponentIndex structure.
/// These tests assert spec compliance from Nethermind's perspective.
/// Cross-client interoperability testing with go-ethereum / fluffy is pending availability
/// of reference .erae test files (go-ethereum has not yet written Proof entries as of 2026-03).
/// </summary>
public class EraFileFormatComplianceTests
{
    // TLV entry header: [type: uint16 LE] [length: uint32 LE] [reserved: uint16 LE = 0x0000]
    private const int EntryHeaderSize = 8;

    // Entry type codes per EraE spec (matches EntryTypes.cs)
    private const ushort TypeVersion = 0x3265;
    private const ushort TypeCompressedHeader = 0x03;
    private const ushort TypeCompressedBody = 0x04;
    private const ushort TypeCompressedSlimReceipts = 0x0a;
    private const ushort TypeProof = 0x0b;
    private const ushort TypeTotalDifficulty = 0x06;
    private const ushort TypeAccumulatorRoot = 0x07;
    private const ushort TypeComponentIndex = 0x3267;

    [Test]
    public async Task File_PreMergeEpoch_FirstEntryIsVersion()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        List<EntryRecord> entries = ReadAllEntries(file.FilePath);

        entries[0].Type.Should().Be(TypeVersion, "Version must be the first entry per spec");
        entries[0].Length.Should().Be(0, "Version entry carries no data");
    }

    [Test]
    public async Task File_PreMergeEpoch_LastEntryIsComponentIndex()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        List<EntryRecord> entries = ReadAllEntries(file.FilePath);

        entries[^1].Type.Should().Be(TypeComponentIndex, "ComponentIndex must be the last entry per spec");
    }

    [Test]
    public async Task File_PostMergeEpoch_LastEntryIsComponentIndex()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 0, postMergeCount: 2);
        List<EntryRecord> entries = ReadAllEntries(file.FilePath);

        entries[^1].Type.Should().Be(TypeComponentIndex);
    }

    [Test]
    public async Task File_AllEntries_HaveReservedBytesZero()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        byte[] bytes = File.ReadAllBytes(file.FilePath);

        long pos = 0;
        while (pos + EntryHeaderSize <= bytes.Length)
        {
            ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)pos + 6, 2));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)pos + 2, 4));

            reserved.Should().Be(0, $"reserved bytes at offset {pos} must be 0x0000 per TLV spec");
            pos += EntryHeaderSize + length;
        }
    }

    [Test]
    public async Task File_PreMergeEpoch_SectionOrderIsHeaderBodyReceiptsProofTdAccumulatorIndex()
    {
        const int blockCount = 2;
        using TempEraFile file = await TempEraFile.Create(preMergeCount: blockCount, postMergeCount: 0);
        List<ushort> types = ReadAllEntries(file.FilePath).Select(e => e.Type).ToList();

        // Section ordering: all headers before any body
        types.LastIndexOf(TypeCompressedHeader).Should().BeLessThan(
            types.IndexOf(TypeCompressedBody), "all headers must precede any body per spec");

        // All bodies before any receipts
        types.LastIndexOf(TypeCompressedBody).Should().BeLessThan(
            types.IndexOf(TypeCompressedSlimReceipts), "all bodies must precede any receipts per spec");

        // All receipts before any proofs
        types.LastIndexOf(TypeCompressedSlimReceipts).Should().BeLessThan(
            types.IndexOf(TypeProof), "all receipts must precede any proof entries per spec");

        // All proofs before TotalDifficulty
        types.LastIndexOf(TypeProof).Should().BeLessThan(
            types.IndexOf(TypeTotalDifficulty), "proof entries must precede TotalDifficulty per spec");

        // TotalDifficulty before AccumulatorRoot
        types.LastIndexOf(TypeTotalDifficulty).Should().BeLessThan(
            types.IndexOf(TypeAccumulatorRoot), "TotalDifficulty must precede AccumulatorRoot per spec");

        // AccumulatorRoot before ComponentIndex
        types.IndexOf(TypeAccumulatorRoot).Should().BeLessThan(
            types.LastIndexOf(TypeComponentIndex), "AccumulatorRoot must precede ComponentIndex per spec");
    }

    [Test]
    public async Task File_PostMergeEpoch_HasNoProofTdOrAccumulatorEntries()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        List<ushort> types = ReadAllEntries(file.FilePath).Select(e => e.Type).ToList();

        types.Should().NotContain(TypeProof, "post-merge epochs have no Proof entries");
        types.Should().NotContain(TypeTotalDifficulty, "post-merge epochs have no TotalDifficulty entries");
        types.Should().NotContain(TypeAccumulatorRoot, "post-merge epochs have no AccumulatorRoot entry");
    }

    [Test]
    public async Task File_PreMergeEpoch_EntryCountsMatchBlockCount()
    {
        const int blockCount = 3;
        using TempEraFile file = await TempEraFile.Create(preMergeCount: blockCount, postMergeCount: 0);
        List<ushort> types = ReadAllEntries(file.FilePath).Select(e => e.Type).ToList();

        types.Count(t => t == TypeCompressedHeader).Should().Be(blockCount);
        types.Count(t => t == TypeCompressedBody).Should().Be(blockCount);
        types.Count(t => t == TypeCompressedSlimReceipts).Should().Be(blockCount);
        types.Count(t => t == TypeProof).Should().Be(blockCount);
        types.Count(t => t == TypeTotalDifficulty).Should().Be(blockCount);
        types.Count(t => t == TypeAccumulatorRoot).Should().Be(1);
        types.Count(t => t == TypeComponentIndex).Should().Be(1);
    }

    [Test]
    public async Task File_PostMergeEpoch_EntryCountsMatchBlockCount()
    {
        const int blockCount = 3;
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 0, postMergeCount: blockCount);
        List<ushort> types = ReadAllEntries(file.FilePath).Select(e => e.Type).ToList();

        types.Count(t => t == TypeCompressedHeader).Should().Be(blockCount);
        types.Count(t => t == TypeCompressedBody).Should().Be(blockCount);
        types.Count(t => t == TypeCompressedSlimReceipts).Should().Be(blockCount);
    }

    [Test]
    public async Task File_AccumulatorRootEntry_Is32Bytes()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        EntryRecord accEntry = ReadAllEntries(file.FilePath).Single(e => e.Type == TypeAccumulatorRoot);

        accEntry.Length.Should().Be(32, "AccumulatorRoot entry must be exactly 32 bytes (Bytes32)");
    }

    [Test]
    public async Task File_TotalDifficultyEntries_AreEach32Bytes()
    {
        using TempEraFile file = await TempEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        List<EntryRecord> tdEntries = ReadAllEntries(file.FilePath)
            .Where(e => e.Type == TypeTotalDifficulty)
            .ToList();

        tdEntries.Should().AllSatisfy(e =>
            e.Length.Should().Be(32, "TotalDifficulty entry must be 32-byte LE uint256"));
    }

    private static List<EntryRecord> ReadAllEntries(string filePath)
    {
        List<EntryRecord> entries = [];
        byte[] bytes = File.ReadAllBytes(filePath);
        long pos = 0;

        while (pos + EntryHeaderSize <= bytes.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)pos, 2));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)pos + 2, 4));
            entries.Add(new EntryRecord(type, length, pos));
            pos += EntryHeaderSize + length;
        }

        return entries;
    }

    private sealed record EntryRecord(ushort Type, uint Length, long Offset);

    private sealed class TempEraFile : IDisposable
    {
        private readonly TempPath _tmpFile;
        public string FilePath => _tmpFile.Path;

        private TempEraFile(TempPath tmpFile) => _tmpFile = tmpFile;

        public static async Task<TempEraFile> Create(int preMergeCount, int postMergeCount)
        {
            TempPath tmpFile = TempPath.GetTempFile();
            using EraWriter writer = new(tmpFile.Path, MainnetSpecProvider.Instance);
            HeaderDecoder headerDecoder = new();
            long number = 0;
            UInt256 td = BlockHeaderBuilder.DefaultDifficulty;

            for (int i = 0; i < preMergeCount; i++, number++, td += BlockHeaderBuilder.DefaultDifficulty)
            {
                TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
                Block block = Build.A.Block.WithNumber(number).WithTotalDifficulty(td).TestObject;
                block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                    [receipt], MainnetSpecProvider.Instance.GetSpec(block.Header), block.ReceiptsRoot);
                block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
                await writer.Add(block, [receipt]);
            }

            for (int i = 0; i < postMergeCount; i++, number++)
            {
                TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
                Block block = Build.A.Block.WithNumber(number).WithPostMergeRules().TestObject;
                block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                    [receipt], MainnetSpecProvider.Instance.GetSpec(block.Header), block.ReceiptsRoot);
                block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);
                await writer.Add(block, [receipt]);
            }

            await writer.Finalize();
            return new TempEraFile(tmpFile);
        }

        public void Dispose() => _tmpFile.Dispose();
    }
}
