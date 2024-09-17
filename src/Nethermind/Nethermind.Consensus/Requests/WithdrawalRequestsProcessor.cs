// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

// https://eips.ethereum.org/EIPS/eip-7002#block-processing
public class WithdrawalRequestsProcessor(ITransactionProcessor transactionProcessor) : IWithdrawalRequestsProcessor
{
    private const long GasLimit = 30_000_000L;

    public IEnumerable<WithdrawalRequest> ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state, Block block)
    {
        if (!spec.IsEip7002Enabled)
            yield break;

        Address eip7002Account = spec.Eip7002ContractAddress;
        if (!state.AccountExists(eip7002Account))
            yield break;

        CallOutputTracer tracer = new();

        Transaction? transaction = new()
        {
            Value = UInt256.Zero,
            Data = Array.Empty<byte>(),
            To = spec.Eip7002ContractAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = GasLimit,
            GasPrice = UInt256.Zero,
        };
        transaction.Hash = transaction.CalculateHash();

        transactionProcessor.Execute(transaction, new BlockExecutionContext(block.Header), tracer);
        var result = tracer.ReturnValue;
        if (result == null || result.Length == 0)
            yield break;

        int sizeOfClass = 20 + 48 + 8;
        int count = result.Length / sizeOfClass;
        for (int i = 0; i < count; ++i)
        {
            WithdrawalRequest request = new();
            Span<byte> span = new Span<byte>(result, i * sizeOfClass, sizeOfClass);
            request.SourceAddress = new Address(span.Slice(0, 20).ToArray());
            request.ValidatorPubkey = span.Slice(20, 48).ToArray();
            request.Amount = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(68, 8));

            yield return request;
        }
    }
}
