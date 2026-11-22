// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed class NetRpcModule(RpcService service) : INetRpcModule
{
    public ResultWrapper<string> net_version() => ResultWrapper<string>.Success(service.ChainId.ToString(CultureInfo.InvariantCulture));
    public ResultWrapper<bool> net_listening() => ResultWrapper<bool>.Success(service.Listening);
}
