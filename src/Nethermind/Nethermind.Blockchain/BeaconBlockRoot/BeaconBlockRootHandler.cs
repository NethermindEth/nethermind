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
    public void ApplyContractStateChanges(Block block, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsBeaconBlockRootAvailable ||
            block.IsGenesis ||
            block.Header.ParentBeaconBlockRoot is null)
            return;

        Address eip4788Account = spec.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress;

        if (!stateProvider.AccountExists(eip4788Account))
            return;

        UInt256 timestamp = (UInt256)block.Timestamp;
        Hash256 parentBeaconBlockRoot = block.ParentBeaconBlockRoot;

        UInt256.Mod(timestamp, Eip4788Constants.HistoryBufferLength, out UInt256 timestampReduced);
        UInt256 rootIndex = timestampReduced + Eip4788Constants.HistoryBufferLength;

        StorageCell tsStorageCell = new(eip4788Account, timestampReduced);
        StorageCell brStorageCell = new(eip4788Account, rootIndex);

        stateProvider.Set(tsStorageCell, Bytes.WithoutLeadingZeros(timestamp.ToBigEndian()).ToArray());
        stateProvider.Set(brStorageCell, Bytes.WithoutLeadingZeros(parentBeaconBlockRoot.Bytes).ToArray());
    }
}
