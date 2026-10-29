// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.TrustedInput;

[RpcModule(TrustedInputPlugin.ModuleType)]
public interface ITrustedInputRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        Description = "Returns the trusted zkVM guest input of a block: the block and its execution witness in the trusted-input format. A block not captured while it was processed is re-executed.",
        IsSharable = true,
        ExampleResponse = "0x4e54494e01000000...")]
    ResultWrapper<byte[]> trustedinput_getInput(BlockParameter blockParameter);
}
