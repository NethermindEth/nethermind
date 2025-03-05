// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.JsonRpc.Modules.Web3;

public class Web3RpcModule : IWeb3RpcModule
{
    private static string _clientId;

    public Web3RpcModule(ILogManager logManager, INetworkConfig networkConfig)
    {
        _clientId = ProductInfo.FormatClientId(networkConfig.ClientIdHiddenParts);
    }

    public ResultWrapper<string> web3_clientVersion() => ResultWrapper<string>.Success(_clientId);

    public ResultWrapper<Hash256> web3_sha3(byte[] data)
    {
        Hash256 keccak = Keccak.Compute(data);
        return ResultWrapper<Hash256>.Success(keccak);
    }
}
