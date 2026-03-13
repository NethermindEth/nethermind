// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.EraE.E2Store;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.EraE.Archive;

/// <summary>
/// Main reader for EraE files. Thread-safe: multiple threads may read concurrently.
/// </summary>
public sealed class EraReader : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private readonly ReceiptMessageDecoder _slimReceiptDecoder = new(skipBloom: true);
    private readonly ReceiptMessageDecoder _fullReceiptDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = BlockBodyDecoder.Instance;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly E2StoreReader _fileReader;

    public long FirstBlock => _fileReader.First;
    public long LastBlock => _fileReader.LastBlock;

    public EraReader(string fileName) : this(new E2StoreReader(fileName)) { }

    public EraReader(E2StoreReader e2)
    {
        _fileReader = e2;
    }

    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        for (long blockNumber = _fileReader.First; blockNumber <= _fileReader.LastBlock; blockNumber++)
        {
            (Block block, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, cancellation);
            yield return (block, receipts);
        }
    }

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < _fileReader.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than first block {_fileReader.First}.");
        if (number > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot exceed last block {_fileReader.LastBlock}.");

        return await ReadBlockAndReceipts(number, cancellation);
    }

    public ValueHash256 ReadAccumulatorRoot()
    {
        long offset = _fileReader.AccumulatorRootOffset;
        if (offset < 0)
            throw new EraException("This EraE file does not contain an AccumulatorRoot (post-merge epoch).");

        _fileReader.ReadEntryAndDecode<ValueHash256>(
            offset,
            static buffer => new ValueHash256(buffer.Span),
            EntryTypes.AccumulatorRoot,
            out ValueHash256 root);

        return root;
    }

    /// <summary>
    /// Verifies the content of this file:
    /// <list type="bullet">
    ///   <item>Block body matches header (transactions root, uncles hash).</item>
    ///   <item>Slim receipt root reconstructed from logs matches header ReceiptsRoot.</item>
    ///   <item>Computed accumulator root matches stored AccumulatorRoot (pre-merge epochs).</item>
    ///   <item>If <paramref name="validator"/> is provided: computed accumulator root is verified
    ///         against the trusted accumulator set (chain integrity check).</item>
    /// </list>
    /// </summary>
    public async Task<ValueHash256> VerifyContent(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        int verifyConcurrency = 0,
        Validator? validator = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        if (verifyConcurrency <= 0) verifyConcurrency = Environment.ProcessorCount;

        long startBlock = _fileReader.First;
        int blockCount = (int)_fileReader.BlockCount;

        // Store (hash, td, isPreMerge) so the accumulator only covers pre-merge blocks.
        using ArrayPoolList<(Hash256 Hash, UInt256 Td, bool IsPreMerge)> blockMeta = new(blockCount, blockCount);

        ConcurrentQueue<long> blockNumbers = new();
        for (long n = startBlock; n <= _fileReader.LastBlock; n++)
            blockNumbers.Enqueue(n);

        using ArrayPoolList<Task> workers = Enumerable.Range(0, verifyConcurrency)
            .Select(_ => Task.Run(async () =>
            {
                while (blockNumbers.TryDequeue(out long blockNumber))
                {
                    (Block block, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, cancellation);

                    if (!blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error))
                        throw new EraVerificationException($"Mismatched block body against header: {error}. Block {blockNumber}.");

                    if (!blockValidator.ValidateOrphanedBlock(block, out error))
                        throw new EraVerificationException($"Invalid block {blockNumber}: {error}.");

                    Hash256 receiptRoot = ReceiptTrie.CalculateRoot(
                        specProvider.GetReceiptSpec(block.Number), receipts, _fullReceiptDecoder);
                    if (block.Header.ReceiptsRoot != receiptRoot)
                        throw new EraVerificationException($"Mismatched receipt root at block {blockNumber}.");

                    int idx = (int)(block.Header.Number - startBlock);
                    blockMeta[idx] = (block.Header.Hash!, block.TotalDifficulty ?? UInt256.Zero, !block.Header.IsPostMerge);
                }
            }, cancellation))
            .ToPooledList(verifyConcurrency);

        await Task.WhenAll(workers.AsSpan());

        // Accumulator verification applies to pre-merge and transition epochs only.
        // Post-merge-only epochs have no AccumulatorRoot entry.
        if (!_fileReader.HasTotalDifficulty)
            return default;

        ValueHash256 storedRoot = ReadAccumulatorRoot();

        using AccumulatorCalculator calculator = new();
        foreach ((Hash256 hash, UInt256 td, bool isPreMerge) in blockMeta.AsSpan())
        {
            if (!isPreMerge) continue; // post-merge blocks (with TTD) are excluded from accumulator
            calculator.Add(hash, td);
        }

        ValueHash256 computedRoot = calculator.ComputeRoot();
        if (computedRoot != storedRoot)
            throw new EraVerificationException("Computed accumulator root does not match stored AccumulatorRoot.");

        // Optional chain-integrity check: verify stored root against externally trusted accumulators.
        if (validator is not null && !validator.VerifyAccumulator(startBlock, storedRoot))
            throw new EraVerificationException("Stored AccumulatorRoot does not match trusted accumulator.");

        return storedRoot;
    }

    public ValueHash256 CalculateChecksum() => _fileReader.CalculateChecksum();

    public void Dispose() => _fileReader.Dispose();

    private async Task<(Block, TxReceipt[])> ReadBlockAndReceipts(long blockNumber, CancellationToken cancellation)
    {
        long headerPos = _fileReader.HeaderOffset(blockNumber);
        (BlockHeader header, _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            headerPos, DecodeHeader, EntryTypes.CompressedHeader, cancellation);
        // IsPostMerge is not RLP-serialized; restore it from Difficulty after decode.
        // This holds for all EIP-3675 networks: pre-merge blocks have Difficulty > 0,
        // post-merge blocks have Difficulty == 0. Non-mainnet genesis blocks with
        // Difficulty == 0 would be mislabeled, but EraE files are only written for
        // post-genesis blocks or chains that follow EIP-3675 semantics.
        header.IsPostMerge = header.Difficulty == 0;

        long bodyPos = _fileReader.BodyOffset(blockNumber);
        (BlockBody body, _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            bodyPos, DecodeBody, EntryTypes.CompressedBody, cancellation);

        long receiptsPos = _fileReader.SlimReceiptsOffset(blockNumber);
        (TxReceipt[] receipts, _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            receiptsPos, DecodeSlimReceipts, EntryTypes.CompressedSlimReceipts, cancellation);

        if (_fileReader.HasTotalDifficulty)
        {
            long tdPos = _fileReader.TotalDifficultyOffset(blockNumber);
            _fileReader.ReadEntryAndDecode(
                tdPos,
                static buf => new UInt256(buf.Span, isBigEndian: false),
                EntryTypes.TotalDifficulty,
                out UInt256 td);
            header.TotalDifficulty = td;
        }

        // Bloom is not stored in slim receipts; TxReceipt.Bloom auto-calculates from Logs on first access.
        return (new Block(header, body), receipts);
    }

    private BlockHeader DecodeHeader(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);
        return _headerDecoder.Decode(ref ctx)!;
    }

    private BlockBody DecodeBody(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);
        return _blockBodyDecoder.Decode(ref ctx)!;
    }

    private TxReceipt[] DecodeSlimReceipts(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);

        int outerLength = ctx.ReadSequenceLength();
        int outerEnd = ctx.Position + outerLength;
        int count = ctx.PeekNumberOfItemsRemaining(outerEnd);

        TxReceipt[] receipts = new TxReceipt[count];
        for (int i = 0; i < count; i++)
            receipts[i] = DecodeOneSlimReceipt(ref ctx);

        return receipts;
    }

    /// <summary>
    /// Decodes a single slim receipt, handling both Nethermind format and go-ethereum EraE format.
    ///
    /// Nethermind writes slim receipts as:
    ///   - Legacy:  RLP sequence [status, cumulative_gas, logs]
    ///   - Typed:   RLP byte-array wrapper (EIP-2718 envelope) containing type_byte + [status, cumulative_gas, logs]
    ///
    /// go-ethereum writes slim receipts as RLP sequences with 4 fields:
    ///   [tx_type, status, cumulative_gas, logs]
    ///   where tx_type=0 (legacy) and status=0 (failure) are each encoded as 0x80 (empty byte string).
    /// </summary>
    private TxReceipt DecodeOneSlimReceipt(ref Rlp.ValueDecoderContext ctx)
    {
        // Nethermind typed receipt: encoded as an RLP byte-array (not a sequence)
        if (!ctx.IsSequenceNext())
            return _slimReceiptDecoder.Decode(ref ctx);

        // Peek the field count inside the sequence without consuming any bytes.
        int savedPosition = ctx.Position;
        int sequenceLength = ctx.ReadSequenceLength();
        int receiptEnd = ctx.Position + sequenceLength;
        int fieldCount = ctx.PeekNumberOfItemsRemaining(receiptEnd);
        ctx.Position = savedPosition;

        if (fieldCount != 4)
        {
            // Nethermind 3-field format: delegate to existing slim decoder
            return _slimReceiptDecoder.Decode(ref ctx);
        }

        // go-ethereum 4-field format: [tx_type, status, cumulative_gas, logs]
        ctx.ReadSequenceLength(); // consume the sequence header (matches the peek above)

        TxReceipt receipt = new();

        byte[] txTypeBytes = ctx.DecodeByteArray();
        receipt.TxType = txTypeBytes.Length == 0 ? TxType.Legacy : (TxType)txTypeBytes[0];

        byte[] statusBytes = ctx.DecodeByteArray();
        receipt.StatusCode = statusBytes.Length == 0 ? (byte)0 : statusBytes[0];

        receipt.GasUsedTotal = ctx.DecodePositiveLong();

        int logsEnd = ctx.ReadSequenceLength() + ctx.Position;
        int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd);
        LogEntry[] logs = new LogEntry[logCount];
        for (int i = 0; i < logCount; i++)
            logs[i] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
        receipt.Logs = logs;

        ctx.Position = receiptEnd;
        return receipt;
    }
}
