using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Precompiles.Stateful;
public class BeaconBlockRootPrecompile : IPrecompile<BeaconBlockRootPrecompile>
{
    public static Address Address { get; } = Address.FromNumber(0x0B);
    public static UInt256 HISTORICAL_ROOTS_LENGTH = 98304;

    public static BeaconBlockRootPrecompile Instance => new BeaconBlockRootPrecompile();

    private byte[] SloadFromStorage(IWorldState state, in UInt256 index)
    {
        StorageCell storageCell = new(Address, index);
        return state.Get(storageCell);
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, IWorldState state)
    {
        Metrics.BeaconBlockRootPrecompile++;

        if (inputData.Length != Keccak.Size)
        {
            return (Array.Empty<byte>(), false);
        }

        UInt256 timestamp = new UInt256(inputData.Span, true);
        UInt256.Mod(timestamp, HISTORICAL_ROOTS_LENGTH, out UInt256 timestampReduced);
        UInt256 recordedTimestamp = new UInt256(SloadFromStorage(state, timestampReduced), true);

        if (recordedTimestamp != timestamp)
        {
            return (default, true);
        }
        else
        {
            UInt256 timestampExtended = timestampReduced + HISTORICAL_ROOTS_LENGTH;
            byte[] recordedRoot = SloadFromStorage(state, timestampExtended);
            return (recordedRoot, true);
        }
    }

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return GasCostOf.BeaconBlockRootPrecompile;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }
}
