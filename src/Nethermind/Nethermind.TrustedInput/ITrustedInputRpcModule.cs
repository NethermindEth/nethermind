// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.TrustedInput;

/// <summary>Provides blocks and their execution witnesses in the trusted zkVM input format.</summary>
[RpcModule(TrustedInputPlugin.ModuleType)]
public interface ITrustedInputRpcModule : IRpcModule
{
    /// <summary>Returns the requested block's captured input, generating it on demand when absent from the cache.</summary>
    [JsonRpcMethod(IsImplemented = true,
        Description = "Returns the trusted zkVM guest input of a block: the block and its execution witness in the trusted-input format. A block not captured while it was processed is re-executed.",
        IsSharable = true,
        ExampleResponse = "0x4e54494e01000000...")]
    ResultWrapper<byte[]> trustedinput_getInput(BlockParameter blockParameter);
}
