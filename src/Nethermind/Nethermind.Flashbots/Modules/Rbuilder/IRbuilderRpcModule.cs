// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
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

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/src/tx.rs#L22
/// </remarks>
public sealed class RevmTransaction
{
    [JsonPropertyName("tx_type")]
    public byte TxType { get; init; } = 0;

    [JsonPropertyName("caller")]
    public Address Caller { get; init; } = Address.Zero; // TODO: Verify default

    [JsonPropertyName("gas_limit")]
    public UInt64 GasLimit { get; init; } = 30_000_000;

    [JsonPropertyName("gas_price")]
    public UInt128 GasPrice { get; init; } = 0;

    [JsonPropertyName("kind")]
    public Address Kind { get; init; } = Address.Zero; // TODO: Verify default

    [JsonPropertyName("value")]
    public UInt256 Value { get; init; } = 0;

    [JsonPropertyName("data")]
    public byte[] Data { get; init; } = [];

    [JsonPropertyName("nonce")]
    public UInt64 Nonce { get; init; } = 0;

    [JsonPropertyName("chan_id")]
    public UInt64 ChainId { get; init; } = 1; // Mainnet chain ID is 1

    [JsonPropertyName("access_list")]
    public AccessListForRpc AccessList { get; init; } = AccessListForRpc.Empty;

    [JsonPropertyName("gas_priority_fee")]
    public UInt128? GasPriorityFee { get; init; } = null;

    [JsonPropertyName("blob_hashes")]
    public byte[][]? BlobHashes { get; init; } = [];

    [JsonPropertyName("max_fee_per_blob_gas")]
    public UInt128 MaxFeePerBlobGas { get; init; } = 0;

    [JsonPropertyName("authorization_list")]
    public AuthorizationListForRpc AuthorizationList { get; init; } = AuthorizationListForRpc.Empty;

    public Transaction ToTransaction()
    {
        // TODO: Dangerous casts
        return new Transaction
        {
            Type = (TxType)TxType,
            SenderAddress = Caller,
            GasLimit = (long)GasLimit,
            GasPrice = (ulong)GasPrice,
            To = Kind,
            Value = Value,
            Data = Data,
            Nonce = Nonce,
            ChainId = ChainId,
            AccessList = AccessList.ToAccessList(),
            DecodedMaxFeePerGas = (ulong?)GasPriorityFee ?? UInt256.Zero,
            BlobVersionedHashes = BlobHashes,
            MaxFeePerBlobGas = (ulong)MaxFeePerBlobGas,
            AuthorizationList = AuthorizationList.ToAuthorizationList(),
        };
    }
}


/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/bundle_state.rs#L408
/// </remarks>
public sealed class BundleState
{
    [JsonPropertyName("state")]
    public IReadOnlyDictionary<Address, BundleAccount> State { get; init; } = new Dictionary<Address, BundleAccount>();

    [JsonPropertyName("contracts")]
    public IReadOnlyDictionary<byte[], object> Contracts { get; init; } = new Dictionary<byte[], object>(); // TODO: Adjust types

    [JsonPropertyName("reverts")]
    public IReadOnlyList<object> Reverts { get; init; } = []; // TODO: Adjust type
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/bundle_account.rs#L20
/// </remarks>
public sealed class BundleAccount
{
    [JsonPropertyName("info")]
    public AccountInfo? Info { get; init; } = new();

    [JsonPropertyName("original_info")]
    public AccountInfo? OriginalInfo { get; init; } = new();

    [JsonPropertyName("storage")]
    public IReadOnlyDictionary<UInt256, StorageSlot> Storage { get; init; } = new Dictionary<UInt256, StorageSlot>();

    [JsonPropertyName("account_status")]
    public AccountStatus AccountStatus { get; init; }

    public AccountChange ToAccountChange()
    {
        // TODO: We might want to do this on the `rbuilder` side.
        // See: https://github.com/NethermindEth/rbuilder/blob/e680a898c57f2626bf75f87f0859ff8772d444cc/crates/rbuilder/src/provider/ipc_state_provider.rs#L453
        return new AccountChange
        {
            ChangedSlots = Storage.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.PresentValue),
            SelfDestructed = AccountStatus
                is AccountStatus.Destroyed
                or AccountStatus.DestroyedChanged
                or AccountStatus.DestroyedAgain,
            Balance = Info?.Balance,
            Nonce = Info?.Nonce,
            CodeHash = Info?.CodeHash,
        };
    }
}

/// <remarks>
/// https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/state/src/account_info.rs#L13
/// </remarks>
public sealed class AccountInfo
{
    [JsonPropertyName("balance")]
    public UInt256 Balance { get; init; } = UInt256.Zero;

    [JsonPropertyName("nonce")]
    public UInt64 Nonce { get; init; } = 0;

    [JsonPropertyName("code_hash")]
    public Hash256 CodeHash { get; init; } = Hash256.Zero;

    [JsonPropertyName("code")]
    public byte[]? Code { get; init; } = null;
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/account_status.rs#L19
/// </remarks>
public enum AccountStatus
{
    LoadedNotExisting,
    Loaded,
    LoadedEmptyEIP161,
    InMemoryChange,
    Changed,
    Destroyed,
    DestroyedChanged,
    DestroyedAgain,
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/plain_account.rs#L31
/// </remarks>
public sealed class StorageSlot
{
    [JsonPropertyName("previous_or_original_value")]
    public UInt256 PreviousOrOriginalValue { get; init; } = UInt256.Zero;

    [JsonPropertyName("present_value")]
    public UInt256 PresentValue { get; init; } = UInt256.Zero;
}
