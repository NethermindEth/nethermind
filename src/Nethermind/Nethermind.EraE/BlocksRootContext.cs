using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.BlockProofs;
using Nethermind.Serialization;

namespace Nethermind.EraE;


public enum AccumulatorType {
    HistoricalHashesAccumulator,
    HistoricalRoots,
    HistoricalSummaries
}


public class BlocksRootContext: IDisposable {
    private static readonly ForkActivation ParisFork = new(MainnetSpecProvider.ParisBlockNumber);
    private static readonly ForkActivation ShanghaiFork = new(long.MaxValue, MainnetSpecProvider.ShanghaiBlockTimestamp);

    private readonly ArrayPoolList<ValueHash256> _blockRoots = new(8192);
    private readonly ArrayPoolList<ValueHash256> _stateRoots = new(8192);
    private readonly ArrayPoolList<(Hash256, UInt256)> _blockHashes = new(8192);
    public readonly AccumulatorType AccumulatorType;

    private ValueHash256? _accumulatorRoot;
    private HistoricalSummary? _historicalSummary;
    private ValueHash256? _historicalRoot;
    public bool Populated { get; private set; }

    public long StartingBlockNumber { get; private set; }
    public ulong? StartingBlockTimestamp { get; private set; }

    public ValueHash256 AccumulatorRoot => _accumulatorRoot ?? throw new InvalidOperationException("Accumulator root not set or not finalized");
    public HistoricalSummary HistoricalSummary => _historicalSummary ?? throw new InvalidOperationException("Historical summary not set or not finalized");
    public ValueHash256 HistoricalRoot => _historicalRoot ?? throw new InvalidOperationException("Historical root not set or not finalized");

    public BlocksRootContext(long startingBlockNumber, ulong? startingBlockTimestamp = null) {
        var forkActivation = new ForkActivation(startingBlockNumber, startingBlockTimestamp);
        AccumulatorType = GetAccumulatorType(forkActivation);
        StartingBlockNumber = startingBlockNumber;
        StartingBlockTimestamp = startingBlockTimestamp;
    }

    public void Dispose() {
        _blockRoots.Dispose();
        _stateRoots.Dispose();
        _blockHashes.Dispose();
    }


    private static AccumulatorType GetAccumulatorType(ForkActivation forkActivation)
    {
        if (forkActivation < ParisFork)
            return AccumulatorType.HistoricalHashesAccumulator;
        if (forkActivation < ShanghaiFork)
            return AccumulatorType.HistoricalRoots;
        return AccumulatorType.HistoricalSummaries;
    }

    private (ValueHash256, ValueHash256) GetCLRootsForELBlock(Block block) {
        // TODO: implement this method
        return (new ValueHash256(), new ValueHash256());
    }


    public void ProcessBlock(Block block) {
        switch (AccumulatorType) {
            case AccumulatorType.HistoricalHashesAccumulator:
                _blockHashes.Add((block.Header.Hash!, block.TotalDifficulty!.Value));
                break;
            default:
                return;
                // This branch is not used for now, because we only process pre-merge blocks
                // (ValueHash256 beaconBlockRoot, ValueHash256 stateRoot) = await GetCLRootsForELBlock(block);
                // _blockRoots.Add(beaconBlockRoot);
                // _stateRoots.Add(stateRoot);
                // break;
        }
        Populated = true;
    }

    public void FinalizeContext() {
        if (!Populated) return;
        switch (AccumulatorType) {
            case AccumulatorType.HistoricalHashesAccumulator:
                AccumulatorCalculator calculator = new();
                foreach ((Hash256, UInt256) valueTuple in _blockHashes.AsSpan())
                {
                    calculator.Add(valueTuple.Item1, valueTuple.Item2);
                }
                _accumulatorRoot = calculator.ComputeRoot();
                break;
            case AccumulatorType.HistoricalRoots:
                SszEncoding.Merkleize(HistoricalBatch.From(_blockRoots.ToArray(), _stateRoots.ToArray()), out UInt256 root);
                _historicalRoot = new ValueHash256(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1)));
                break;
            case AccumulatorType.HistoricalSummaries:
                SszEncoding.Merkleize(ValueHash256Vector.From(_blockRoots.ToArray()), out UInt256 blockRoot);
                SszEncoding.Merkleize(ValueHash256Vector.From(_stateRoots.ToArray()), out UInt256 stateRoot);
                _historicalSummary = new HistoricalSummary(
                    new ValueHash256(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref blockRoot, 1))),
                    new ValueHash256(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref stateRoot, 1)))
                );
                break;
        }
    }
}
