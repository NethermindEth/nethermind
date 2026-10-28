// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule
{
    public async Task<ResultWrapper<Hash256[]>> eth_sendProofWrapper(byte[] wrapper)
    {
        if (proofWrapperService?.IsEnabled != true)
            return ResultWrapper<Hash256[]>.Fail("EIP-8288 proof wrappers are unavailable.", ErrorCodes.MethodNotFound);
        if (wrapper.Length > Eip8288Constants.MaxWrapperBytes)
            return ResultWrapper<Hash256[]>.Fail("Proof wrapper exceeds MAX_WRAPPER_BYTES.", ErrorCodes.InvalidInput);
        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        Result<Hash256[]> result;
        try
        {
            result = (await proofWrapperService.AcceptDetailedAsync(wrapper, timeout.Token)).Result;
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<Hash256[]>.Fail("Proof wrapper admission timed out.", ErrorCodes.Timeout);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
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
