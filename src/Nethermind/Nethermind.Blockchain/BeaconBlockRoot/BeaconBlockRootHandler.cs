// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public class BeaconBlockRootHandler : IBeaconBlockRootHandler
{
    private readonly ITransactionProcessor _processor;
    private static Address Default4788Address = Address.FromNumber(0x0b);
    private readonly ILogger _logger;
    private const long GasLimit = 30_000_000L;
    public BeaconBlockRootHandler(
        ITransactionProcessor processor,
        ILogManager logManager)
    {
        _processor = processor;
        _logger = logManager.GetClassLogger();
    }
    public void ScheduleSystemCall(Block block, IReleaseSpec spec, IWorldState stateProvider)
    {
        BlockHeader? header = block.Header;
        if (!spec.IsBeaconBlockRootAvailable ||
            header.IsGenesis ||
            header.ParentBeaconBlockRoot is null) return;

        Transaction? transaction = new()
        {
            Value = UInt256.Zero,
            Data = header.ParentBeaconBlockRoot.Bytes.ToArray(),
            To = spec.Eip4788ContractAddress ?? Default4788Address,
            SenderAddress = Address.SystemUser,
            GasLimit = GasLimit,
            GasPrice = UInt256.Zero,
        };
        transaction.Hash = transaction.CalculateHash();

        try
        {
            _processor.Execute(transaction, header, NullTxTracer.Instance);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error during calling BeaconBlockRoot contract", e);
        }
    }
}
