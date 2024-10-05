// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private const int SizeOfClass = 20 + 48 + 8;

    public IEnumerable<WithdrawalRequest> ReadWithdrawalRequests(Block block, IWorldState state, IReleaseSpec spec)
    {
        if (!spec.IsEip7002Enabled || !state.AccountExists(spec.Eip7002ContractAddress))
        {
            yield break;
        }

        byte[]? result = ExecuteTransaction(block, spec);
        if (result?.Length > 0)
        {
            int count = result.Length / SizeOfClass;
            for (int i = 0; i < count; ++i)
            {
                Memory<byte> memory = result.AsMemory(i * SizeOfClass, SizeOfClass);
                WithdrawalRequest request = new()
                {
                    SourceAddress = new Address(memory.Slice(0, 20).AsArray()),
                    ValidatorPubkey = memory.Slice(20, 48),
                    Amount = BinaryPrimitives.ReadUInt64BigEndian(memory.Slice(68, 8).Span)
                };
                yield return request;
            }
        }
    }

    private byte[]? ExecuteTransaction(Block block, IReleaseSpec spec)
    {
        CallOutputTracer tracer = new();
        Transaction transaction = new()
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

        return tracer.ReturnValue;
    }
}
