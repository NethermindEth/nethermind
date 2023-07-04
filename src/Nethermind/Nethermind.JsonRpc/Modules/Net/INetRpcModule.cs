// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Net
{
    [RpcModule(ModuleType.Net)]
    public interface INetRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "", ExampleResponse = "0x247b5f5f007fb5d50de13cfcbd4460db21c12bcb")]
        ResultWrapper<Address> net_localAddress();

        [JsonRpcMethod(Description = "", ExampleResponse = "enode://a9cfa3cb16b537e131b0f141b5ef0c0ab9bf0dbec7799c3fc7bf8a974ff3e74e9b3258951b285dfed07ab395049bcd65fed96116bb92561612682551ec458497@18.193.43.58:30303")]
        ResultWrapper<string> net_localEnode();

        [JsonRpcMethod(Description = "", ExampleResponse = "4")]
        ResultWrapper<string> net_version();

        [JsonRpcMethod(Description = "", ExampleResponse = "true")]
        ResultWrapper<bool> net_listening();

        [JsonRpcMethod(Description = "", ExampleResponse = "0x11")]
        ResultWrapper<long> net_peerCount();
    }
}
