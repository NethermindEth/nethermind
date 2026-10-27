// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        Result<Hash256[]> result = (await proofWrapperService.AcceptDetailedAsync(wrapper)).Result;
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
