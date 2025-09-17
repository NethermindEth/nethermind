// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Flashbots.Modules.Rbuilder;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/src/tx.rs#L22
/// </remarks>
public sealed class RevmTransaction
{
    [JsonPropertyName("tx_type")] public byte TxType { get; init; } = 0;

    [JsonPropertyName("caller")] public Address Caller { get; init; } = Address.Zero; // TODO: Verify default

    [JsonPropertyName("gas_limit")] public UInt64 GasLimit { get; init; } = 30_000_000;

    [JsonPropertyName("gas_price")] public UInt128 GasPrice { get; init; } = 0;

    [JsonPropertyName("kind")] public Address Kind { get; init; } = Address.Zero; // TODO: Verify default

    [JsonPropertyName("value")] public UInt256 Value { get; init; } = 0;

    [JsonPropertyName("data")] public byte[] Data { get; init; } = [];

    [JsonPropertyName("nonce")] public UInt64 Nonce { get; init; } = 0;

    [JsonPropertyName("chan_id")] public UInt64 ChainId { get; init; } = 1; // Mainnet chain ID is 1

    [JsonPropertyName("access_list")] public AccessListForRpc AccessList { get; init; } = AccessListForRpc.Empty;

    [JsonPropertyName("gas_priority_fee")] public UInt128? GasPriorityFee { get; init; } = null;

    [JsonPropertyName("blob_hashes")] public byte[][]? BlobHashes { get; init; } = [];

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
    public IReadOnlyDictionary<byte[], object> Contracts { get; init; } =
        new Dictionary<byte[], object>(); // TODO: Adjust types

    [JsonPropertyName("reverts")] public IReadOnlyList<object> Reverts { get; init; } = []; // TODO: Adjust type
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/bundle_account.rs#L20
/// </remarks>
public sealed class BundleAccount
{
    [JsonPropertyName("info")] public RevmStateAccountInfo? Info { get; init; } = new();

    [JsonPropertyName("original_info")] public RevmStateAccountInfo? OriginalInfo { get; init; } = new();

    [JsonPropertyName("storage")]
    public IReadOnlyDictionary<UInt256, StorageSlot> Storage { get; init; } = new Dictionary<UInt256, StorageSlot>();

    [JsonPropertyName("account_status")] public DatabaseAccountStatus DatabaseAccountStatus { get; init; }

    public AccountChange ToAccountChange()
    {
        // TODO: We might want to do this on the `rbuilder` side.
        // See: https://github.com/NethermindEth/rbuilder/blob/e680a898c57f2626bf75f87f0859ff8772d444cc/crates/rbuilder/src/provider/ipc_state_provider.rs#L453
        return new AccountChange
        {
            ChangedSlots = Storage.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.PresentValue),
            SelfDestructed = DatabaseAccountStatus
                is DatabaseAccountStatus.Destroyed
                or DatabaseAccountStatus.DestroyedChanged
                or DatabaseAccountStatus.DestroyedAgain,
            Balance = Info?.Balance,
            Nonce = Info?.Nonce,
            CodeHash = Info?.CodeHash,
        };
    }
}

/// <remarks>
/// https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/state/src/account_info.rs#L13
/// </remarks>
public sealed class RevmStateAccountInfo
{
    [JsonPropertyName("balance")] public UInt256 Balance { get; init; } = UInt256.Zero;

    [JsonPropertyName("nonce")] public UInt64 Nonce { get; init; } = 0;

    [JsonPropertyName("code_hash")] public Hash256 CodeHash { get; init; } = Hash256.Zero;

    [JsonPropertyName("code")] public byte[]? Code { get; init; } = null;
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/database/src/states/account_status.rs#L19
/// </remarks>
public enum DatabaseAccountStatus
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

    [JsonPropertyName("present_value")] public UInt256 PresentValue { get; init; } = UInt256.Zero;
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/interface/src/result.rs#L48
/// </remarks>
public sealed class RevmExecutionResultAndState
{
    public required RevmExecutionResult Result { get; init; }
    public required IReadOnlyDictionary<Address, RevmStateAccount> State { get; init; }
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/state/src/lib.rs#L22
/// </remarks>
public sealed class RevmStateAccount
{
    public RevmStateAccountInfo Info { get; init; } = new();
    public IReadOnlyDictionary<UInt256, RevmStorageSlot> Storage { get; init; } = [];
    public byte Status { get; init; } = RevmStateAccountStatus.Loaded;
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/state/src/lib.rs#L338
/// </remarks>
public sealed class RevmStorageSlot
{
    [JsonPropertyName("original_value")] public UInt256 OriginalValue { get; init; }
    [JsonPropertyName("present_value")] public UInt256 PresentValue { get; init; }
    [JsonPropertyName("is_cold")] public bool IsCold { get; init; }
}

/// <remarks>
/// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/state/src/lib.rs#L306
/// </remarks>
public static class RevmStateAccountStatus
{
    public const byte Loaded = 0b00000000;
    public const byte Created = 0b00000001;
    public const byte SelfDestructed = 0b00000010;
    public const byte Touched = 0b00000100;
    public const byte LoadedAsNotExisting = 0b00010000;
    public const byte Cold = 0b00100000;
}

/// <remarks>
/// https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/interface/src/result.rs#L48
/// </remarks>
public sealed class RevmExecutionResult
{
    public RevmExecutionResultSuccess? Success { get; init; } = null;
    public RevmExecutionResultRevert? Revert { get; init; } = null;
    public RevmExecutionResultHalt? Halt { get; init; } = null;
}

public sealed class RevmExecutionResultSuccess
{
    public required ResultSuccessReason Reason { get; init; }
    [JsonPropertyName("gas_used")] public required UInt64 GasUsed { get; init; }
    [JsonPropertyName("gas_refunded")] public required UInt64 GasRefunded { get; init; }
    public required IReadOnlyList<RevmLog> Logs { get; init; }
    public required ResultSuccessOutput Output { get; init; }

    /// <remarks>
    /// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/interface/src/result.rs#L559
    /// </remarks>
    public enum ResultSuccessReason
    {
        Stop,
        Return,
        SelfDestruct,
        EofReturnContract,
    }

    /// <remarks>
    /// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/interface/src/result.rs#L182
    /// </remarks>
    public class ResultSuccessOutput
    {
        public byte[]? Call { get; init; } = null;
        public (byte[], Address?)? Create { get; init; } = null;
    }
}

public sealed class RevmExecutionResultRevert
{
    [JsonPropertyName("gas_used")] public UInt64 GasUsed { get; init; }
    public byte[] Output { get; init; } = [];
}

public sealed class RevmExecutionResultHalt
{
    [JsonPropertyName("gas_used")] public required UInt64 GasUsed { get; init; }

    /// <remarks>
    /// See: https://github.com/bluealloy/revm/blob/0ca6564f02004976f533cacf8821fed09d801e0a/crates/context/interface/src/result.rs#L573
    /// </remarks>
    public required string Reason { get; init; } // TODO: This type is more complex
}

/// <remarks>
/// See: https://github.com/alloy-rs/core/blob/e122aeb365e1dceadcbcea41dde7db8f2e67c827/crates/primitives/src/log/mod.rs#L119
/// </remarks>
public sealed class RevmLog
{
    public Address Address { get; init; } = Address.Zero;
    public RevmLogData Data { get; init; } = new();
}

/// <remarks>
/// See: https://github.com/alloy-rs/core/blob/e122aeb365e1dceadcbcea41dde7db8f2e67c827/crates/primitives/src/log/mod.rs#L20
/// </remarks>
public class RevmLogData
{
    public IReadOnlyList<Hash256> Topics { get; init; } = [];
    public byte[] Data { get; init; } = [];
}
