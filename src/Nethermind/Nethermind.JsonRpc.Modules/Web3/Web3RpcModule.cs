// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Web3;

public class Web3RpcModule : IWeb3RpcModule
{
    public Web3RpcModule(ILogManager logManager)
    {
    }

    public ResultWrapper<string> web3_clientVersion() => ResultWrapper<string>.Success(ProductInfo.ClientId);

    public ResultWrapper<Hash256> web3_sha3(byte[] data)
    {
        Hash256 keccak = Keccak.Compute(data);
        return ResultWrapper<Hash256>.Success(keccak);
    }
}
