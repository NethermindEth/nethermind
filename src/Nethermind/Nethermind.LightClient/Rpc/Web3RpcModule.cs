// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed class Web3RpcModule : IWeb3RpcModule
{
    public ResultWrapper<string> web3_clientVersion() => ResultWrapper<string>.Success(ProductInfo.ClientId);
    public ResultWrapper<Hash256> web3_sha3(byte[] data) => ResultWrapper<Hash256>.Success(Keccak.Compute(data));
}
