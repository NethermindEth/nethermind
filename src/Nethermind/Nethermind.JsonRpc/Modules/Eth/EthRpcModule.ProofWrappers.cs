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
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes)
            return ResultWrapper<Hash256[]>.Fail("Proof wrapper exceeds the size limit.", ErrorCodes.InvalidInput);
        Result<Hash256[]> result = await proofWrapperService.AcceptAsync(wrapper);
        return result.IsSuccess ? ResultWrapper<Hash256[]>.Success(result.Data!)
            : ResultWrapper<Hash256[]>.Fail(result.Error!, ErrorCodes.TransactionRejected);
    }

    public async Task<ResultWrapper<Hash256[]>> eth_sendProofInclusionList(byte[] inclusionList)
    {
        if (proofWrapperService?.IsEnabled != true)
            return ResultWrapper<Hash256[]>.Fail("EIP-8288 proof inclusion lists are unavailable.", ErrorCodes.MethodNotFound);
        if (inclusionList.Length > LeanProofStore.MaxWrapperBytes)
            return ResultWrapper<Hash256[]>.Fail("Proof inclusion list exceeds the size limit.", ErrorCodes.InvalidInput);
        Result<Hash256[]> result = await proofWrapperService.AcceptInclusionListAsync(inclusionList);
        return result.IsSuccess ? ResultWrapper<Hash256[]>.Success(result.Data!)
            : ResultWrapper<Hash256[]>.Fail(result.Error!, ErrorCodes.TransactionRejected);
    }

    public ResultWrapper<byte[]> eth_getProofWrapper()
    {
        if (proofWrapperService?.IsEnabled != true)
            return ResultWrapper<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.", ErrorCodes.MethodNotFound);
        Result<byte[]> result = proofWrapperService.BuildWrapper();
        return result.IsSuccess ? ResultWrapper<byte[]>.Success(result.Data!)
            : ResultWrapper<byte[]>.Fail(result.Error!, ErrorCodes.TransactionRejected);
    }
}
