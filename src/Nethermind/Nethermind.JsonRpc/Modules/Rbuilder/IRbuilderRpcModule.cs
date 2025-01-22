// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.RBuilder;

[RpcModule(ModuleType.Rbuilder)]
public interface IRbuilderRpcModule: IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        Description = "Returns bytecode based on hash.",
        IsSharable = true,
        ExampleResponse = "0xffff")]
    ResultWrapper<byte[]> rbuilder_getCodeByHash(Hash256 hash);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Calculate the state root on top of the state trie at specified block given a set of change.",
        IsSharable = true,
        ExampleResponse = "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter block, IDictionary<Address, AccountChange> accountDiff);
}

public class AccountChange
{
    [JsonPropertyName("nonce")]
    public UInt256? Nonce { get; set; }

    [JsonPropertyName("balance")]
    public UInt256? Balance { get; set; }

    [JsonPropertyName("code_hash")]
    public Hash256? CodeHash { get; set; }

    [JsonPropertyName("self_destructed")]
    public bool SelfDestructed { get; set; }

    [JsonPropertyName("changed_slots")]
    public IDictionary<UInt256, UInt256>? ChangedSlots { get; set; }
}
