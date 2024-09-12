// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Transaction object generic to all types.
/// See: https://github.com/ethereum/execution-apis/blob/1d4f70e84191bb574286fd7cea6c48795bf73e78/src/schemas/transaction.yaml#L358
/// </summary>
/// <remarks>
/// This class is intended to be used <b>ONLY</b> as input for RPC calls, not for return values.
/// </remarks>
public record class RpcGenericTransaction
{
    // TODO: To be discussed: this class is esentially a bag of "whatever" that could be part of any Transaction type.
    // Adding new Transaction types (ex. Optimism `Deposit`) will require modifying this class to include the new fields.
    //
    // Another option would be to ignore the spec and instead take the incoming JSON `Type` and deserialize
    // into specific classes (ex. `RpcLegacyTransaction`, `RpcAccessListTransaction`, etc).
    // This involves other complications, for example: The `Blobs` fields can be part of the incoming JSON, but
    // it's not part of the spec of `Blob` transactions, that is `RpcBlobTransaction` does not have a `Blobs` field.

    public TxType? Type { get; set; }

    public UInt256? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    public Address? From { get; set; }

    public long? Gas { get; set; }

    public UInt256? Value { get; set; }

    public byte[]? Input { get; set; }

    public UInt256? GasPrice { get; set; }

    public UInt256? MaxPriorityFeePerGas { get; set; }

    public UInt256? MaxFeePerGas { get; set; }

    public UInt256? MaxFeePerBlobGas { get; set; }

    public RpcAccessList? AccessList { get; set; }

    // TODO: Each item should be a 32 byte array
    // Currently we don't enforce this (hashes can have any length)
    // See `RpcBlobTransaction` also
    public byte[][]? BlobVersionedHashes { get; set; }

    // TODO: Does it match with `ShardBlobNetworkWrapper.Blobs`
    // If so, what about `Commitments` and `Proofs`?
    public byte[]? Blobs { get; set; }

    public ulong? ChainId { get; set; }

    // TODO: Missing Optimism specific fields

    // TODO: We'll replace this method with a proper factory class.
    // Such factory will have a list of converters indexed by `TxType`,
    // and each converter exposes a method to convert from `RpcGenericTransaction` to specific `Transaction` subclasses.
    // Ex. `LegacyTransaction : Transaction` implements `FromRpcGenericTransaction(RpcGenericTransaction rpcTx)`.
    public Transaction ToTransaction()
    {
        // TODO: What does the spec say about missing fields?
        // We're currently "defaulting" everything to `0` or `null`.

        return new Transaction()
        {
            Type = Type ?? TxType.Legacy,
            Nonce = Nonce ?? UInt256.Zero,
            To = To,
            SenderAddress = From ?? Address.Zero,
            GasLimit = Gas ?? 0,
            Value = Value ?? 0,
            Data = Input,
            GasPrice = GasPrice ?? 0,
            DecodedMaxFeePerGas = MaxFeePerGas ?? 0,
            MaxFeePerBlobGas = MaxFeePerBlobGas,
            AccessList = AccessList?.ToAccessList(),
            BlobVersionedHashes = BlobVersionedHashes,
            ChainId = ChainId ?? 0,
            // TODO: What is the point of `MaxPriorityFeePerGas` if it's internally always the same as `GasPrice` ?
            // MaxPriorityFeePerGas = MaxPriorityFeePerGas ?? 0,
            // TODO: How do we map `Blobs`?
            // Blobs = Blobs,
        };
    }
}
