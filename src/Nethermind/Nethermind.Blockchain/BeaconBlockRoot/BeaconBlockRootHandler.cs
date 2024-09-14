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

namespace Nethermind.Blockchain.BeaconBlockRoot;
public class BeaconBlockRootHandler(ITransactionProcessor processor) : IBeaconBlockRootHandler
{
    private const long GasLimit = 30_000_000L;

    public (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = true)
    {
        BlockHeader? header = block.Header;
        bool canInsertBeaconRoot = spec.IsBeaconBlockRootAvailable
                                  && !header.IsGenesis
                                  && header.ParentBeaconBlockRoot is not null;

        Address? eip4788ContractAddress = canInsertBeaconRoot ?
            spec.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress :
            null;

        if (eip4788ContractAddress is null)
        {
            return (null, null);
        }

        var builder = new AccessList.Builder()
            .AddAddress(eip4788ContractAddress);

        if (includeStorageCells)
        {
            builder.AddStorage(block.Timestamp % 8191);
        }

        return (eip4788ContractAddress, builder.Build());
    }

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        (Address? toAddress, AccessList? accessList) = BeaconRootsAccessList(block, spec, includeStorageCells: false);

        if (toAddress is not null)
        {
            BlockHeader? header = block.Header;
            Transaction transaction = new()
            {
                Value = UInt256.Zero,
                Data = header.ParentBeaconBlockRoot.Bytes.ToArray(),
                To = toAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = GasLimit,
                GasPrice = UInt256.Zero,
                AccessList = accessList
            };

            transaction.Hash = transaction.CalculateHash();

            processor.Execute(transaction, header, NullTxTracer.Instance);
        }
    }
}
