// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public class BeaconBlockRootHandler(ITransactionProcessor processor) : IBeaconBlockRootHandler
{
    private const long GasLimit = 30_000_000L;

    public void StoreBeaconRoot(Block block, IReleaseSpec spec, IWorldState worldState)
    {
        BlockHeader? header = block.Header;
        var canInsertBeaconRoot = spec.IsBeaconBlockRootAvailable
                                  && !header.IsGenesis
                                  && header.ParentBeaconBlockRoot is not null;

        if (canInsertBeaconRoot)
        {
            Address beaconRootsAddress = spec.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress;
            Transaction transaction = new()
            {
                Value = UInt256.Zero,
                Data = header.ParentBeaconBlockRoot.Bytes.ToArray(),
                To = beaconRootsAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = GasLimit,
                GasPrice = UInt256.Zero,
                AccessList = new AccessList.Builder().AddAddress(beaconRootsAddress).Build()
            };

            transaction.Hash = transaction.CalculateHash();

            processor.Execute(worldState, transaction, header, NullTxTracer.Instance);
        }
    }
}
