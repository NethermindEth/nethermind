// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Witness
{
    [RpcModule(ModuleType.Witness)]
    public interface IWitnessRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Return witness of Block provided",
            ResponseDescription = "Table of hashes of state nodes that were read during block processing",
            ExampleResponse =
                "\"0x1\"",
            IsImplemented = true)]
        Task<ResultWrapper<Keccak[]>> get_witnesses([JsonRpcParameter(Description = "Block to get witness",
                ExampleValue = "{\"jsonrpc\":\"2.0\",\"result\":[\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\"],\"id\":67}")]
            BlockParameter blockParameter);
    }
}
