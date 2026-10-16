// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule
{
    public async Task<ResultWrapper<Hash256[]>> eth_sendProofWrapper(byte[] wrapper, byte[][]? transactionEnvelopes = null)
    {
        if (proofWrapperService?.IsEnabled != true)
            return ResultWrapper<Hash256[]>.Fail("EIP-8288 proof wrappers are unavailable.", ErrorCodes.MethodNotFound);
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes)
            return ResultWrapper<Hash256[]>.Fail("Proof wrapper exceeds the size limit.", ErrorCodes.InvalidInput);
        Dictionary<ValueHash256, Transaction>? recovered = null;
        if (transactionEnvelopes is not null)
        {
            if (transactionEnvelopes.Length > Eip8288Constants.MaxTxsPerWrapper)
                return ResultWrapper<Hash256[]>.Fail("Too many proof wrapper envelopes.", ErrorCodes.InvalidInput);
            int bytes = 0;
            recovered = new(transactionEnvelopes.Length);
            try
            {
                foreach (byte[] envelope in transactionEnvelopes)
                {
                    if (envelope is null || envelope.Length == 0 || envelope.Length > LeanProofStore.MaxWrapperBytes - bytes)
                        return ResultWrapper<Hash256[]>.Fail("Proof wrapper envelopes exceed the recovery byte limit.", ErrorCodes.InvalidInput);
                    bytes += envelope.Length;
                    Transaction transaction = TxDecoder.Instance.DecodeCompleteNotNull(envelope,
                        RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
                    if (!transaction.SupportsFrames)
                        return ResultWrapper<Hash256[]>.Fail("Proof wrapper envelopes must be frame transactions.", ErrorCodes.InvalidInput);
                    transaction.Hash = transaction.CalculateHash();
                    if (!recovered.TryAdd(transaction.Hash.ValueHash256, transaction))
                        return ResultWrapper<Hash256[]>.Fail("Duplicate proof wrapper envelope hash.", ErrorCodes.InvalidInput);
                }
            }
            catch (Exception exception) when (exception is RlpException or ArgumentException)
            {
                return ResultWrapper<Hash256[]>.Fail("Invalid proof wrapper transaction envelope.", ErrorCodes.InvalidInput);
            }
        }
        Result<Hash256[]> result = (await proofWrapperService.AcceptDetailedAsync(wrapper, recovered)).Result;
        return result.IsSuccess ? ResultWrapper<Hash256[]>.Success(result.Data!)
            : ResultWrapper<Hash256[]>.Fail(result.Error!, ErrorCodes.TransactionRejected);
    }

    public ResultWrapper<byte[]> eth_getProofWrapper()
    {
        if (proofWrapperService?.IsEnabled != true)
            return ResultWrapper<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.", ErrorCodes.MethodNotFound);
        Result<byte[]> result = proofWrapperService.GetLatestWrapper();
        return result.IsSuccess ? ResultWrapper<byte[]>.Success(result.Data!)
            : ResultWrapper<byte[]>.Fail(result.Error!, ErrorCodes.TransactionRejected);
    }
}
