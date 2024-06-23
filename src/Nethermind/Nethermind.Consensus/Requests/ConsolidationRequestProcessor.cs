// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

// https://eips.ethereum.org/EIPS/eip-7251#block-processing
public class ConsolidationRequestsProcessor(ITransactionProcessor transactionProcessor)
{
    private const long GasLimit = 30_000_000L;

    public IEnumerable<ConsolidationRequest> ReadConsolidationRequests(IReleaseSpec spec, IWorldState state, Block block)
    {
        if (!spec.ConsolidationRequestsEnabled)
            yield break;

        Address eip7251Account = spec.Eip7251ContractAddress;
        if (!state.AccountExists(eip7251Account)) // not needed anymore?
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

        int sizeOfClass = 20 + 48 + 48;
        int count = result.Length / sizeOfClass;
        for (int i = 0; i < count; ++i)
        {
            ConsolidationRequest request = new();
            Span<byte> span = new Span<byte>(result, i * sizeOfClass, sizeOfClass);
            request.SourceAddress = new Address(span.Slice(0, 20).ToArray());
            request.SourcePubkey = span.Slice(20, 48).ToArray();
            request.TargetPubkey = span.Slice(68, 48).ToArray();

            yield return request;
        }
    }
}
