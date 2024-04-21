// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.ValidatorExit;

public class WithdrawRequestsHandler
{
    private readonly ITransactionProcessor _transactionProcessor;
    private const long GasLimit = 30_000_000L;

    public WithdrawRequestsHandler(
        ITransactionProcessor transactionProcessor)
    {
        _transactionProcessor = transactionProcessor;
    }
    ValidatorExit[] CalculateValidatorExits(IReleaseSpec spec, IWorldState state, BlockHeader header)
    {
        CallOutputTracer tracer = new();

        try
        {
            Transaction? transaction = new()
            {
                Value = UInt256.Zero,
                Data = Array.Empty<byte>(),
                To = spec.Eip7002ContractAddress, // ToDo set default address
                SenderAddress = Address.SystemUser,
                GasLimit = GasLimit,
                GasPrice = UInt256.Zero,
            };
            transaction.Hash = transaction.CalculateHash();

            _transactionProcessor.Execute(transaction, new BlockExecutionContext(header), tracer);
            var result = tracer.ReturnValue;
            var withdrawalRequests = new List<ValidatorExit>();


            return withdrawalRequests.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
