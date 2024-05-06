// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
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
    WithdrawalRequest[] CalculateValidatorExits(IReleaseSpec spec, IWorldState state, BlockHeader header)
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
            if (result == null || result.Length == 0)
                return Array.Empty<WithdrawalRequest>();

            int sizeOfClass = 20 + 48 + 8;
            int count = result.Length / sizeOfClass;
            var withdrawalRequests = new List<WithdrawalRequest>(count);
            for (int i = 0; i < count; ++i)
            {
                WithdrawalRequest request = new();
                Span<byte> span = new Span<byte>(result, i * sizeOfClass, sizeOfClass);
                request.SourceAddress = new Address(span.Slice(0, 20).ToArray());
                request.ValidatorPubkey = span.Slice(20, 48).ToArray();
                request.Amount = BitConverter.ToUInt64(span.Slice(68, 8));

                withdrawalRequests.Add(request);
            }

            return withdrawalRequests.ToArray();
        }
        catch (Exception)
        {
            // add logger
            return null;
        }
    }
}
