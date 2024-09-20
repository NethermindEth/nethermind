// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

// https://eips.ethereum.org/EIPS/eip-7251#block-processing
public class ConsolidationRequestsProcessor(ITransactionProcessor transactionProcessor)
{
    private const long GasLimit = 30_000_000L;
    private const int SizeOfClass = 20 + 48 + 48;

    public IEnumerable<ConsolidationRequest> ReadConsolidationRequests(IReleaseSpec spec, IWorldState state, Block block)
    {
        if (!spec.ConsolidationRequestsEnabled)
            yield break;

        Address eip7251Account = spec.Eip7251ContractAddress;
        if (!state.AccountExists(eip7251Account))
            yield break;

        CallOutputTracer tracer = new();

        Transaction? transaction = new()
        {
            Value = UInt256.Zero,
            Data = Array.Empty<byte>(),
            To = spec.Eip7251ContractAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = GasLimit,
            GasPrice = UInt256.Zero,
        };
        transaction.Hash = transaction.CalculateHash();

        transactionProcessor.Execute(transaction, new BlockExecutionContext(block.Header), tracer);
        var result = tracer.ReturnValue;
        if (result == null || result.Length == 0)
            yield break;

        Memory<byte> memory = result.AsMemory();
        int count = memory.Length / SizeOfClass;

        for (int i = 0; i < count; ++i)
        {
            int offset = i * SizeOfClass;
            ConsolidationRequest request = new()
            {
                SourceAddress = new Address(memory.Slice(offset, 20).ToArray()),
                SourcePubkey = memory.Slice(offset + 20, 48),
                TargetPubkey = memory.Slice(offset + 68, 48)
            };

            yield return request;
        }
    }
}
