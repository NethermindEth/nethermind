// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.Precompiles.Stateful;
using Nethermind.Int256;
using Nethermind.State;
using static Nethermind.Evm.Precompiles.Stateful.BeaconBlockRootPrecompile;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.BeaconBlockRoot;
public class BeaconBlockRootHandler : IBeaconBlockRootHandler
{
    public static Address SystemUser { get; } = new("0xfffffffffffffffffffffffffffffffffffffffe");

    public void InitStatefulPrecompiles(Block block, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsBeaconBlockRootAvailable) return;

        stateProvider.CreateAccountIfNotExists(SystemUser, 0, 1);

        if (block.Header.ParentBeaconBlockRoot is null)
        {
            return;
        }

        UInt256 timestamp = (UInt256)block.Timestamp;
        Keccak parentBeaconBlockRoot = block.ParentBeaconBlockRoot;

        UInt256.Mod(timestamp, HISTORICAL_ROOTS_LENGTH, out UInt256 timestampReduced);
        UInt256 rootIndex = timestampReduced + HISTORICAL_ROOTS_LENGTH;

        StorageCell tsStorageCell = new(BeaconBlockRootPrecompile.Address, timestampReduced);
        StorageCell brStorageCell = new(BeaconBlockRootPrecompile.Address, rootIndex);

        stateProvider.Set(tsStorageCell, timestamp.ToBigEndian());
        stateProvider.Set(brStorageCell, parentBeaconBlockRoot.Bytes.ToArray());
    }
}
