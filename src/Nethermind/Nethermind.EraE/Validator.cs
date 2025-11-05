using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;

namespace Nethermind.EraE;

public class Validator {
    private readonly IHistoricalSummariesProvider? _historicalSummariesProvider;
    private readonly ISet<ValueHash256>? _trustedAccumulators;
    private readonly ISet<ValueHash256>? _trustedHistoricalRoots;
    private readonly SlotTime _slotTime;

    private const int SLOTS_PER_HISTORICAL_ROOT = 8192;
    private const int GEN_INDEX_EXECUTION_BLOCK_PROOF_BELLATRIX = 3228;
    private const int GEN_INDEX_EXECUTION_BLOCK_PROOF_DENEB = 6444;

    public Validator(ISpecProvider specProvider, ISet<ValueHash256>? trustedAccumulators, ISet<ValueHash256>? trustedHistoricalRoots, IHistoricalSummariesProvider? historicalSummariesProvider) {
        _slotTime = new(
            specProvider.BeaconChainGenesisTimestamp!.Value * 1000,
            new Timestamper(),
            // TODO: get slot length from spec or config
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(0));
        _trustedAccumulators = trustedAccumulators;
        _trustedHistoricalRoots = trustedHistoricalRoots;
        _historicalSummariesProvider = historicalSummariesProvider;
    }

    private bool IsDeneb(ulong blockTimestamp) {
        ulong slotNumber = _slotTime.GetSlot(blockTimestamp);
        return slotNumber >= 8_626_176;
    }

    private ValueHash256? GetAccumulator(long blockNumber) {
        return GetAccumulatorForEpoch(blockNumber / SLOTS_PER_HISTORICAL_ROOT);
    }

    private ValueHash256? GetAccumulatorForEpoch(long epochIdx) {
        if (_trustedAccumulators is not null && _trustedAccumulators.Count > epochIdx){
            return _trustedAccumulators.ElementAt((int)epochIdx);
        }
        return null;
    }

    private ValueHash256? GetHistoricalRoot(long slotNumber) {
        long historicalRootIndex = slotNumber / SLOTS_PER_HISTORICAL_ROOT;
        if (_trustedHistoricalRoots is not null && _trustedHistoricalRoots.Count > historicalRootIndex){
            return _trustedHistoricalRoots.ElementAt((int)historicalRootIndex);
        }
        return null;
    }

     private async Task<HistoricalSummary?> GetHistoricalSummary(long slotNumber) {
        long historicalSummaryIndex = slotNumber / SLOTS_PER_HISTORICAL_ROOT;
        Task<HistoricalSummary?>? task = _historicalSummariesProvider?.GetHistoricalSummary((int)historicalSummaryIndex);
        if (task is null) return null;

        HistoricalSummary? summary = await task;
        return summary;
    }

    private async Task<ValueHash256?> GetBlockSummaryRoot(long slotNumber) {
        HistoricalSummary? summary = await GetHistoricalSummary(slotNumber);
        return summary?.BlockSummaryRoot;
    }

    private static void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
    {
        Span<byte> combined = stackalloc byte[a.Length + b.Length];
        a.CopyTo(combined);
        b.CopyTo(combined[a.Length..]);

        SHA256.TryHashData(combined, target, out _);
    }

    private static bool VerifyProof(ValueHash256 leaf, ValueHash256[] branch, int depth, long genIndex, ValueHash256 root) {
        if (branch.Length != depth) return false;

        byte[] merkleRoot = leaf.ToByteArray();
        for (int i = 0; i < depth; i++) {
            bool leftSibling = (genIndex >> i) % 2 == 0;
            if (leftSibling)
                Hash(branch[i].Bytes, merkleRoot, merkleRoot);
            else
                Hash(merkleRoot, branch[i].Bytes, merkleRoot);
        }
        return merkleRoot.SequenceEqual(root.ToByteArray());
    }

    private void VerifyHashesAccumulator(Block block, BlockHeaderProof proof) {
        // load trusted hashes accumulator from local file
        // verify block header hash against the accumulator provided in the erae file or trusted accumulator
        long headerIndex = block.Header.Number % SLOTS_PER_HISTORICAL_ROOT;
        long genIndex = (SLOTS_PER_HISTORICAL_ROOT * 2 * 2) + (headerIndex * 2);
        ValueHash256? accumulatorRoot = GetAccumulator(block.Header.Number);
        if (accumulatorRoot is null) {
            throw new EraVerificationException("Accumulator root not found");
        }
        if (!VerifyProof(block.Header.Hash!, proof.HashesAccumulator!, 15, genIndex, accumulatorRoot.Value)) {
            throw new EraVerificationException("Computed accumulator does not match stored accumulator");
        }
    }

    private bool VerifyExecutionBlockProof(Block block, BlockHeaderProof proof) {
        return VerifyProof(
            block.Header.Hash,
            proof.ExecutionBlockProof!,
            11,
            GEN_INDEX_EXECUTION_BLOCK_PROOF_BELLATRIX,
            proof.BeaconBlockRoot!.Value);
    }

    private bool VerifyExecutionBlockProofPostDeneb(Block block, BlockHeaderProof proof) {
        return VerifyProof(
            block.Header.Hash,
            proof.ExecutionBlockProof!,
            12,
            GEN_INDEX_EXECUTION_BLOCK_PROOF_DENEB,
            proof.BeaconBlockRoot!.Value);
    }

    private void VerifyRoots(Block block, BlockHeaderProof proof) {
        // verify BeaconBlockRoot in the proof against the trusted roots
        long slotNumber = (long)_slotTime.GetSlot(block.Header.Timestamp);
        long blockRootIndex = slotNumber % SLOTS_PER_HISTORICAL_ROOT;
        long genIndex = 2 * SLOTS_PER_HISTORICAL_ROOT + blockRootIndex;
        ValueHash256? historicalRoot = GetHistoricalRoot(slotNumber);
        if (historicalRoot is null) {
            throw new EraVerificationException("Historical root not found");
        }
        // TODO: add beacon block root verification
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 14, genIndex, historicalRoot.Value)) {
            throw new EraVerificationException("Computed historical root does not match stored historical root");
        }
        // verify EL block hash against the proof
        if (!VerifyExecutionBlockProof(block, proof))
            throw new EraVerificationException("Computed execution block root does not match stored execution block root");
    }

    private async Task VerifySummaries(Block block, BlockHeaderProof proof) {
        // load trusted summaries from local file

        long slotNumber = (long)_slotTime.GetSlot(block.Header.Timestamp);
        long genIndex = (SLOTS_PER_HISTORICAL_ROOT + (slotNumber % SLOTS_PER_HISTORICAL_ROOT));

        ValueHash256? blockSummaryRoot = await GetBlockSummaryRoot(slotNumber);
        if (blockSummaryRoot is null) {
            throw new EraVerificationException("Historical block summary root not found");
        }

        // verify BeaconBlockSummaries root in the proof against the trusted summaries
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 13, genIndex, blockSummaryRoot.Value)) {
            throw new EraVerificationException("Computed block root does not match stored historical block summary root");
        }
        // verify EL block hash against the proof
        if (IsDeneb(block.Header.Timestamp)) {
            if (!VerifyExecutionBlockProofPostDeneb(block, proof))
                throw new EraVerificationException("Computed execution block root does not match stored execution block root");
        } else {
            if (!VerifyExecutionBlockProof(block, proof))
                throw new EraVerificationException("Computed execution block root does not match stored execution block root");
        }
    }

    public async Task VerifyContent(Block block, BlockHeaderProof proof)
    {
        switch (proof.ProofType)
        {
            case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator:
                VerifyHashesAccumulator(block, proof);
                break;
            case BlockHeaderProofType.BlockProofHistoricalRoots:
                VerifyRoots(block, proof);
                break;
            case BlockHeaderProofType.BlockProofHistoricalSummaries:
                await VerifySummaries(block, proof);
                break;
        }
    }

    public bool VerifyAccumulator(long blockNumber, ValueHash256 accumulatorRoot) {
        ValueHash256? trustedAccumulatorRoot = GetAccumulator(blockNumber);
        if (trustedAccumulatorRoot is null) {
            throw new EraVerificationException("Accumulator root not found");
        }
        return trustedAccumulatorRoot.Equals(accumulatorRoot);
    }

    public async Task VerifyBlocksRootContext(BlocksRootContext blocksRootContext) {
        switch (blocksRootContext.AccumulatorType) {
            case AccumulatorType.HistoricalHashesAccumulator:
                // verify accumulator root against the trusted accumulator root for the starting block or slot number
                if (!VerifyAccumulator(blocksRootContext.StartingBlockNumber, blocksRootContext.AccumulatorRoot)) {
                    throw new EraVerificationException("Computed accumulator does not match trusted accumulator");
                }
                break;
            case AccumulatorType.HistoricalRoots:
                // verify historical root against the trusted historical root
                long slotNumber = (long)_slotTime.GetSlot(blocksRootContext.StartingBlockTimestamp!.Value);
                ValueHash256? trustedHistoricalRoot = GetHistoricalRoot(slotNumber);
                if (trustedHistoricalRoot is null) {
                    throw new EraVerificationException("Historical root not found");
                }

                if (!trustedHistoricalRoot.Equals(blocksRootContext.HistoricalRoot)) {
                    throw new EraVerificationException("Computed historical root does not match trusted historical root");
                }
                break;
            case AccumulatorType.HistoricalSummaries:
                // verify historical summary against the trusted historical summary
                long _slotNumber = (long)_slotTime.GetSlot(blocksRootContext.StartingBlockTimestamp!.Value);
                HistoricalSummary? trustedHistoricalSummary = await GetHistoricalSummary(_slotNumber);
                if (trustedHistoricalSummary is null) {
                    throw new EraVerificationException("Historical summary not found");
                }

                if (!trustedHistoricalSummary.Value.BlockSummaryRoot.Equals(blocksRootContext.HistoricalSummary.BlockSummaryRoot)) {
                    throw new EraVerificationException("Computed block summary root does not match trusted block summary root");
                }
                if (!trustedHistoricalSummary.Value.StateSummaryRoot.Equals(blocksRootContext.HistoricalSummary.StateSummaryRoot)) {
                    throw new EraVerificationException("Computed state summary root does not match trusted state summary root");
                }
                break;
        }
    }
}
