// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public class BeaconBlockRootHandler(ITransactionProcessor processor, IWorldState stateProvider) : IBeaconBlockRootHandler
{
    private const long GasLimit = 30_000_000L;

    AccessList? IHasAccessList.GetAccessList(Block block, IReleaseSpec spec)
        => BeaconRootsAccessList(block, spec, includeStorageCells: true).accessList;

    public (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = false)
    {
        const int HistoryBufferLength = 8191;

        BlockHeader? header = block.Header;
        bool canInsertBeaconRoot = spec.IsBeaconBlockRootAvailable
                                  && !header.IsGenesis
                                  && header.ParentBeaconBlockRoot is not null;

        Address? eip4788ContractAddress = canInsertBeaconRoot ?
            spec.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress :
            null;

        if (eip4788ContractAddress is null || !stateProvider.AccountExists(eip4788ContractAddress))
        {
            return (null, null);
        }

        var builder = new AccessList.Builder()
            .AddAddress(eip4788ContractAddress);

        if (includeStorageCells)
        {
            // https://eips.ethereum.org/EIPS/eip-4788
            // Set the storage value at header.timestamp % HISTORY_BUFFER_LENGTH to be header.timestamp
            ulong slotIndex = header.Timestamp % HistoryBufferLength;
            UInt256 slot256 = slotIndex;
            builder.AddStorage(in slot256);
            // Set the storage value at header.timestamp % HISTORY_BUFFER_LENGTH + HISTORY_BUFFER_LENGTH to be calldata[0:32]
            slot256 = slotIndex + HistoryBufferLength;
            builder.AddStorage(in slot256);
        }

        return (eip4788ContractAddress, builder.Build());
    }

    public void StoreBeaconRoot(Block block, in BlockExecutionContext blkCtx, IReleaseSpec spec, ITxTracer tracer)
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

            processor.Execute(transaction, in blkCtx, tracer);
        }
    }
}
