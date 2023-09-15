// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.BeaconBlockRoot;
public class BeaconBlockRootHandler : IBeaconBlockRootHandler
{
    public static UInt256 HISTORICAL_ROOTS_LENGTH = 98304;
    private static readonly Address DefaultPbbrContractAddress = new Address("0xbEac00dDB15f3B6d645C48263dC93862413A222D");

    public void ApplyContractStateChanges(Block block, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsBeaconBlockRootAvailable ||
            block.IsGenesis ||
            block.Header.ParentBeaconBlockRoot is null) return;

        UInt256 timestamp = (UInt256)block.Timestamp;
        Keccak parentBeaconBlockRoot = block.ParentBeaconBlockRoot;

        UInt256.Mod(timestamp, HISTORICAL_ROOTS_LENGTH, out UInt256 timestampReduced);
        UInt256 rootIndex = timestampReduced + HISTORICAL_ROOTS_LENGTH;

        StorageCell tsStorageCell = new(spec.Eip4788ContractAddress ?? DefaultPbbrContractAddress, timestampReduced);
        StorageCell brStorageCell = new(spec.Eip4788ContractAddress ?? DefaultPbbrContractAddress, rootIndex);

        stateProvider.Set(tsStorageCell, Bytes.WithoutLeadingZeros(timestamp.ToBigEndian()).ToArray());
        stateProvider.Set(brStorageCell, Bytes.WithoutLeadingZeros(parentBeaconBlockRoot.Bytes).ToArray());
    }
}
