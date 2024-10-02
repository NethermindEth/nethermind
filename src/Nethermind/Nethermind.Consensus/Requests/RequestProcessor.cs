// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

public abstract class RequestProcessor<T>(ITransactionProcessor transactionProcessor)
{
    private const long GasLimit = 30_000_000L;

    public IEnumerable<T> ReadRequests(Block block, IWorldState state, IReleaseSpec spec, Address contractAddress)
    {
        if (!IsEnabledInSpec(spec))
            return Array.Empty<T>();

        if (!state.AccountExists(contractAddress))
            return Array.Empty<T>();

        CallOutputTracer tracer = new();

        Transaction? transaction = new()
        {
            Value = UInt256.Zero,
            Data = Array.Empty<byte>(),
            To = contractAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = GasLimit,
            GasPrice = UInt256.Zero,
        };
        transaction.Hash = transaction.CalculateHash();

        transactionProcessor.Execute(transaction, new BlockExecutionContext(block.Header), tracer);
        var result = tracer.ReturnValue;
        if (result == null || result.Length == 0)
            return Array.Empty<T>();
        return ParseResult(tracer.ReturnValue);
    }


    protected abstract bool IsEnabledInSpec(IReleaseSpec spec);
    protected abstract IEnumerable<T> ParseResult(Memory<byte> result);
}
