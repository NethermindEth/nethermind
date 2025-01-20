// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    public UInt256? Nonce { get; set; }
    public UInt256? Balance { get; set; }
    public byte[]? Code { get; set; }
    public bool SelfDestructed { get; set; }
    public IDictionary<UInt256, Hash256>? ChangedSlots { get; set; }
}
