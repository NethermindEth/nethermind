// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using System.Collections.Concurrent;
using Nethermind.Core.Extensions;
using Nethermind.Era1.Exceptions;
using Nethermind.State.Proofs;

namespace Nethermind.EraE;

/// <summary>
/// Main reader for erae file. Uses E2StoreReader which internally mmap the whole file. This reader is thread safe
/// allowing multiple thread to read from it at the same time.
/// </summary>

// set skipBloom to true, because EraE format omits that field in archive and we need to compute filter locally,
// which is handled in an appropriate class later.
public class EraReader(E2StoreReader e2) : Era1.EraReader(e2, new ReceiptMessageDecoder(skipBloom: true))
{
    private readonly IHistoricalSummariesProvider? _historicalSummariesProvider;
    private readonly ISet<ValueHash256>? _trustedHistoricalRoots;
    private readonly ISet<ValueHash256>? _trustedAccumulators;

    public EraReader(string fileName) : this(new E2StoreReader(fileName))
    {
    }

    public EraReader(
        E2StoreReader e2,
        IHistoricalSummariesProvider? historicalSummariesProvider,
        ISet<ValueHash256>? trustedHistoricalRoots,
        ISet<ValueHash256>? trustedAccumulators
    ) : this(e2) {
        _historicalSummariesProvider = historicalSummariesProvider;
        _trustedHistoricalRoots = trustedHistoricalRoots;
        _trustedAccumulators = trustedAccumulators;
    }


    protected readonly ProofDecoder _proofDecoder = new();

    async Task<BlockHeaderProof?> ReadProof(ulong slot, CancellationToken cancellationToken)
    {
        BlockOffset blockOffset = ((E2StoreReader)_fileReader).BlockOffset((long)slot);
        // if proof is available for a specific block, decode one.
        if (blockOffset.ProofPosition is null) return null;
        (BlockHeaderProof proof, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            blockOffset.ProofPosition.Value,
            DecodeProof,
            EntryTypes.Proof,
            cancellationToken
        );
        return proof;
    }

    /// <summary>
    /// Verify the content of this file. Notably, it verify that the accumulator match, but not necessarily trusted.
    /// It also validate the block with IBlockValidator. Ideally all verification is done here so that
    /// the file is easy to read directly without having to import.
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the expected accumulator matches, and <see cref="false"/> if there is no match.</returns>
    public new async Task<bool> VerifyContent(ISpecProvider specProvider, IBlockValidator blockValidator, int verifyConcurrency = 0, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(specProvider);

        Validator blockProofValidator = new(specProvider, _trustedAccumulators, _trustedHistoricalRoots, _historicalSummariesProvider);
        SlotTime slotTime = new(
            specProvider.BeaconChainGenesisTimestamp!.Value * 1000,
            new Timestamper(),
            // TODO: get slot length from spec or config
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(0));

        if (verifyConcurrency == 0) verifyConcurrency = Environment.ProcessorCount;

        long startBlock = _fileReader.First;

        ValueHash256? accumulator = ReadAccumulator();
        if (accumulator is not null) {
            if (!blockProofValidator.VerifyAccumulator(startBlock, accumulator.Value)) {
                throw new EraVerificationException("Computed accumulator does not match provided accumulator");
            }
        }

        BlocksRootContext blocksRootContext = new(startBlock);

        var blockNumbers = new ConcurrentQueue<long>(EnumerateBlockNumber());

        using ArrayPoolList<Task> workers = Enumerable.Range(0, verifyConcurrency).Select((_) => Task.Run(async () =>
        {
            while (blockNumbers.TryDequeue(out long blockNumber))
            {
                EntryReadResult? result = await ReadBlockAndReceipts(blockNumber, true, cancellation);
                EntryReadResult err = result.Value;

                if (!blockValidator.ValidateBodyAgainstHeader(err.Block.Header, err.Block.Body, out string? error))
                {
                    throw new EraVerificationException($"Mismatched block body againts header: {error}. Block number {blockNumber}.");
                }

                if (!blockValidator.ValidateOrphanedBlock(err.Block, out error))
                {
                    throw new EraVerificationException($"Invalid block {error}");
                }

                Hash256 receiptRoot = ReceiptTrie.CalculateRoot(specProvider.GetReceiptSpec(err.Block.Number), err.Receipts, _receiptDecoder);
                if (err.Block.Header.ReceiptsRoot != receiptRoot)
                {
                    throw new EraVerificationException($"Mismatched receipt root. Block number {blockNumber}.");
                }

                // Remove this branch once we start processing post-merge blocks.
                if (!err.Block.IsPoS())
                {
                    // we only process pre-merge blocks to compute accumulator root and compare one with trusted accumulator root.
                    blocksRootContext.ProcessBlock(err.Block);
                }

                var slotNumber = err.Block.Header.IsPoS() ? (ulong)blockNumber : slotTime.GetSlot(err.Block.Header.Timestamp);
                // read proof for this block

                if (await ReadProof(slotNumber, cancellation) is { } proof) {
                    await blockProofValidator.VerifyContent(err.Block, proof);
                } else {
                    // proof is not available for this block, skip verification
                    // TODO: allow user to specify if they want to skip verification for blocks without proof.
                    // if skipping is allowed, we need to verify the blocks root context against the trusted blocks root context
                    // later in the code.
                    continue;
                }
            }
        }, cancellation)).ToPooledList(verifyConcurrency);
        await Task.WhenAll(workers.AsSpan());

        // For post-merge blocks, context wouldn't be populated, so we skip verification.
        if (blocksRootContext.Populated) {
            // finalize the blocks root context to compute the accumulator root, historical root, and historical summary.
            blocksRootContext.FinalizeContext();
            // verify the blocks root context against the trusted blocks root context in case when individual block proofs are not provided in EraE file.
            await blockProofValidator.VerifyBlocksRootContext(blocksRootContext);
        }

        return true;
    }

    public new ValueHash256? ReadAccumulator()
    {
        try {
            _ = _fileReader.ReadEntryAndDecode(
                ((E2StoreReader)_fileReader).AccumulatorOffset,
                static (buffer) => new ValueHash256(buffer.Span),
                EntryTypes.Accumulator,
                out ValueHash256 hash);

            return hash;
        }
        catch (Era1.EraException) {
            return null; // accumulator is not available for this era
        }
        catch (Exception e) {
            throw new EraVerificationException($"Failed to read accumulator from erae file: {e.Message}");
        }
    }

    protected override async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.First
            || blockNumber > _fileReader.First + _fileReader.BlockCount)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));
        // cast to E2StoreReader to access the BlockOffset method which is overridden in EraE
        BlockOffset blockOffset = ((E2StoreReader)_fileReader).BlockOffset(blockNumber);

        (BlockHeader header, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            blockOffset.HeaderPosition,
            DecodeHeader,
            EntryTypes.CompressedHeader,
            cancellationToken);

        (BlockBody body, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            blockOffset.BodyPosition,
            DecodeBody,
            EntryTypes.CompressedBody,
            cancellationToken);

        (TxReceipt[] receipts, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            blockOffset.ReceiptsPosition,
            DecodeReceipts,
            EntryTypes.CompressedSlimReceipts,
            cancellationToken);

        // if total difficulty is available for a specific block, decode one.
        if (blockOffset.TotalDifficultyPosition is not null) {
            _ = _fileReader.ReadEntryAndDecode(
                blockOffset.TotalDifficultyPosition.Value,
                static (buffer) => new UInt256(buffer.Span, isBigEndian: false),
                EntryTypes.TotalDifficulty,
                out UInt256 currentTotalDiffulty);
            header.TotalDifficulty = currentTotalDiffulty;
        }

        var block = new Block(header, body);
        return new EntryReadResult(block, receipts);
    }

    protected BlockHeaderProof DecodeProof(Memory<byte> buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.Span);
        return _proofDecoder.Decode(ref ctx, RlpBehaviors.None)!;
    }
}
