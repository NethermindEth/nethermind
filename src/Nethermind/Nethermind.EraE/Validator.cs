using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;

namespace Nethermind.EraE;

public class Validator {
    private readonly ISet<ValueHash256>? _trustedAccumulators;
    private readonly ISet<ValueHash256>? _trustedHistoricalRoots;
    private readonly ValueHash256[] _historicalRoots;
    private readonly ValueHash256[] _summaryRoots;
    private readonly SlotTime _slotTime;
    
    private const int SLOTS_PER_HISTORICAL_ROOT = 8192;
    private const int GEN_INDEX_EXECUTION_BLOCK_PROOF_BELLATRIX = 3228;
    private const int GEN_INDEX_EXECUTION_BLOCK_PROOF_DENEB = 6444;

    public Validator(ISpecProvider specProvider, ISet<ValueHash256>? trustedAccumulators, ISet<ValueHash256>? trustedHistoricalRoots) {
        _slotTime = new(
            specProvider.BeaconChainGenesisTimestamp!.Value * 1000, 
            new Timestamper(),
            // TODO: get slot length from spec or config
            TimeSpan.FromSeconds(12), 
            TimeSpan.FromSeconds(0));
        _trustedAccumulators = trustedAccumulators;
        _trustedHistoricalRoots = trustedHistoricalRoots;
    }

    private bool IsDeneb(ulong blockTimestamp) {
        ulong slotNumber = _slotTime.GetSlot(blockTimestamp);
        if (slotNumber is >= 8_626_176 and < 11_649_024) {
            return true;
        }
        return false;
    }

    private ValueHash256 GetAccumulator(long blockNumber) {
        long epochIdx = blockNumber / SLOTS_PER_HISTORICAL_ROOT;
        return _trustedAccumulators![epochIdx];
    }

    private ValueHash256 GetHistoricalRoot(long slotNumber) {
        long historicalRootIndex = slotNumber / SLOTS_PER_HISTORICAL_ROOT;
        return _historicalRoots[historicalRootIndex];
    }

    private ValueHash256 GetSummaryRoot(long slotNumber) {
        long historicalRootIndex = slotNumber / SLOTS_PER_HISTORICAL_ROOT;
        return _summaryRoots[historicalRootIndex];
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

    private async Task VerifyHashesAccumulator(Block block, BlockHeaderProof proof, ValueHash256? root = null) {
        // load trusted hashes accumulator from local file
        // verify block header hash against the accumulator provided in the erae file or trusted accumulator
        long headerIndex = block.Header.Number % SLOTS_PER_HISTORICAL_ROOT;
        long genIndex = (SLOTS_PER_HISTORICAL_ROOT * 2 * 2) + (headerIndex * 2);
        ValueHash256 accumulatorRoot = root ?? GetAccumulator(block.Header.Number);
        if (!VerifyProof(block.Header.Hash!, proof.HashesAccumulator!, 15, genIndex, accumulatorRoot.ToHash256())) {
            throw new EraVerificationException("Computed accumulator does not match stored accumulator");
        }
    }

    private bool VerifyExecutionBlockProof(Block block, BlockHeaderProof proof) {
        return VerifyProof(
            block.Header.Hash!, 
            proof.ExecutionBlockProof!, 
            11, 
            GEN_INDEX_EXECUTION_BLOCK_PROOF_BELLATRIX, 
            proof.BeaconBlockRoot!.Value);
    }

    private bool VerifyExecutionBlockProofPostDeneb(Block block, BlockHeaderProof proof) {
        return VerifyProof(
            block.Header.Hash!, 
            proof.ExecutionBlockProof!, 
            12, 
            GEN_INDEX_EXECUTION_BLOCK_PROOF_DENEB, 
            proof.BeaconBlockRoot!.Value);
    }

    private async Task VerifyRoots(Block block, BlockHeaderProof proof) {
        // verify BeaconBlockRoot in the proof against the trusted roots   
        long slotNumber = (long)_slotTime.GetSlot(block.Header.Timestamp);
        long blockRootIndex = slotNumber % SLOTS_PER_HISTORICAL_ROOT;
        long genIndex = 2 * SLOTS_PER_HISTORICAL_ROOT + blockRootIndex;
        ValueHash256 historicalRoot = GetHistoricalRoot(slotNumber);
        // TODO: add beacon block root verification
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 14, genIndex, historicalRoot)) {
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
        
        // verify BeaconBlockSummaries root in the proof against the trusted summaries
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 13, genIndex, GetSummaryRoot(slotNumber))) {
            throw new EraVerificationException("Computed historical root does not match stored historical root");
        }
        // verify EL block hash against the proof
        // TODO: check range instead of equality
        if (IsDeneb(block.Header.Timestamp)) {
            if (!VerifyExecutionBlockProofPostDeneb(block, proof))
                throw new EraVerificationException("Computed execution block root does not match stored execution block root");
        } else {
            if (!VerifyExecutionBlockProof(block, proof))
                throw new EraVerificationException("Computed execution block root does not match stored execution block root");
        }
    }

    public async Task VerifyContent(Block block, BlockHeaderProof proof, ValueHash256? accumulatorRoot = null)
    {
        switch (proof.ProofType)
        {
            case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator:
                await VerifyHashesAccumulator(block, proof, accumulatorRoot);
                break;
            case BlockHeaderProofType.BlockProofHistoricalRoots:
                await VerifyRoots(block, proof);
                break;
            case BlockHeaderProofType.BlockProofHistoricalSummaries:
                await VerifySummaries(block, proof);
                break;
        }
    }
}
