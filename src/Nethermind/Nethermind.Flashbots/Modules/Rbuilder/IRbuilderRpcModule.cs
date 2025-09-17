// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Flashbots.Data;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Flashbots.Modules.Rbuilder;

[RpcModule(ModuleType.Rbuilder)]
public interface IRbuilderRpcModule
    : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        Description = "Returns bytecode based on hash.",
        IsSharable = true,
        ExampleResponse = "0xffff")]
    ResultWrapper<byte[]?> rbuilder_getCodeByHash(Hash256 hash);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Calculate the state root on top of the state trie at specified block given a set of change.",
        IsSharable = true,
        ExampleResponse = "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter block,
        IDictionary<Address, AccountChange> accountDiff);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get account data",
        IsSharable = true)]
    ResultWrapper<AccountState?> rbuilder_getAccount(Address address, BlockParameter block);


    [JsonRpcMethod(IsImplemented = true,
        Description = "Gets block hash",
        IsSharable = true,
        ExampleResponse = "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    ResultWrapper<Hash256?> rbuilder_getBlockHash(BlockParameter block);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Execute a call on top of the state trie at specified block given a set of changes.",
        IsSharable = false,
        ExampleResponse = "0xffff")]
    ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> rbuilder_transact(
        RevmTransaction revmTransaction, BundleState bundleState);
}

/// <remarks>
/// https://github.com/NethermindEth/rbuilder/blob/e680a898c57f2626bf75f87f0859ff8772d444cc/crates/rbuilder/src/provider/ipc_state_provider.rs#L438
/// </remarks>
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


public class AccountState
{
    public AccountState(UInt256 nonce, UInt256 balance, ValueHash256 codeHash)
    {
        Nonce = nonce;
        Balance = balance;
        CodeHash = new Hash256(codeHash);
    }

    public AccountState()
    {
        Nonce = 0;
        Balance = 0;
        CodeHash = Keccak.OfAnEmptyString;
    }

    [JsonPropertyName("nonce")]
    public UInt256 Nonce { get; set; }

    [JsonPropertyName("balance")]
    public UInt256 Balance { get; set; }

    [JsonPropertyName("code_hash")]
    public Hash256 CodeHash { get; set; }
}
