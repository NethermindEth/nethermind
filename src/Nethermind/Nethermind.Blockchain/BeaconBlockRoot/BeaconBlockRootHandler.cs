// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public class BeaconBlockRootHandler(
    ITransactionProcessor processor,
    ILogManager? logManager)
    : IBeaconBlockRootHandler
{
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger();
    private const long GasLimit = 30_000_000L;

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        BlockHeader? header = block.Header;
        var canInsertBeaconRoot = spec.IsBeaconBlockRootAvailable
                                  && !header.IsGenesis
                                  && header.ParentBeaconBlockRoot is not null;

        if (spec.Eip4788ContractAddress is null)
            throw new ArgumentException("Eip4788ContractAddress shouldn't be null");

        if (canInsertBeaconRoot)
        {
            Transaction transaction = new()
            {
                Value = UInt256.Zero,
                Data = header.ParentBeaconBlockRoot.Bytes.ToArray(),
                To = spec.Eip4788ContractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = GasLimit,
                GasPrice = UInt256.Zero,
            };

            transaction.Hash = transaction.CalculateHash();

            try
            {
                processor.Execute(transaction, header, NullTxTracer.Instance);
            }
            catch (Exception e)
            {
                throw new BlockchainException("Error during calling BeaconBlockRoot contract", e);
            }
        }
    }
}
