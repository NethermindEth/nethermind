// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// A transaction in its mempool (network) form: the canonical transaction plus the blob sidecar it travels with,
/// and the in-memory state the pool tracks for it.
/// </summary>
/// <param name="Transaction">The canonical transaction, as included in a block.</param>
/// <param name="NetworkWrapper">The blob sidecar, or <c>null</c> when the transaction travels without one.</param>
/// <remarks>
/// See <see href="https://eips.ethereum.org/EIPS/eip-4844#networking"/>.
/// The tracking properties are never encoded on the wire; the pool assigns them on admission and keeps them
/// on the instance it holds.
/// </remarks>
public sealed record MempoolTransaction(Transaction Transaction, ShardBlobNetworkWrapper? NetworkWrapper)
{
    public Hash256? Hash { get => Transaction.Hash; set => Transaction.Hash = value; }

    public bool SupportsBlobs => Transaction.SupportsBlobs;

    public bool SupportsFrames => Transaction.SupportsFrames;

    public bool CarriesBlobs => Transaction.CarriesBlobs;

    public TxType Type { get => Transaction.Type; set => Transaction.Type = value; }

    public Address? SenderAddress { get => Transaction.SenderAddress; set => Transaction.SenderAddress = value; }

    public ulong Nonce { get => Transaction.Nonce; set => Transaction.Nonce = value; }

    public UInt256 Timestamp { get => Transaction.Timestamp; set => Transaction.Timestamp = value; }

    public ulong GasLimit { get => Transaction.GasLimit; set => Transaction.GasLimit = value; }

    public Address? To => Transaction.To;

    public UInt256 Value => Transaction.Value;

    public ref readonly UInt256 ValueRef => ref Transaction.ValueRef;

    public Signature? Signature => Transaction.Signature;

    public UInt256 GasPrice { get => Transaction.GasPrice; set => Transaction.GasPrice = value; }

    public ref readonly UInt256 MaxPriorityFeePerGas => ref Transaction.MaxPriorityFeePerGas;

    public UInt256 DecodedMaxFeePerGas { get => Transaction.DecodedMaxFeePerGas; set => Transaction.DecodedMaxFeePerGas = value; }

    public UInt256 MaxFeePerGas => Transaction.MaxFeePerGas;

    public UInt256? MaxFeePerBlobGas => Transaction.MaxFeePerBlobGas;

    public AuthorizationTuple[]? AuthorizationList => Transaction.AuthorizationList;

    [MemberNotNullWhen(true, nameof(AuthorizationList))]
    public bool HasAuthorizationList => Transaction.HasAuthorizationList;

    public UInt256 MaxFee { get => Transaction.MaxFee; set => Transaction.MaxFee = value; }

    public byte[]?[]? BlobVersionedHashes { get => Transaction.BlobVersionedHashes; set => Transaction.BlobVersionedHashes = value; }

    public UInt256[]? NonceKeys { get => Transaction.NonceKeys; set => Transaction.NonceKeys = value; }

    public TxFrame[]? Frames { get => Transaction.Frames; set => Transaction.Frames = value; }

    public string ToString(string indent) => Transaction.ToString(indent);

    public string ToShortString() => Transaction.ToShortString();

    public void ClearPreHash() => Transaction.ClearPreHash();

    public void CopyTo(Transaction tx) => Transaction.CopyTo(tx);

    public static implicit operator MempoolTransaction(Transaction transaction) => new(transaction, null);

    /// <summary>
    /// The order in which the transaction arrived at the pool.
    /// </summary>
    /// <remarks>Used for sorting in edge cases.</remarks>
    public ulong PoolIndex { get; set; }

    /// <summary>
    /// The lowest effective gas price among this transaction and the sender's pending transactions ahead of it.
    /// </summary>
    public UInt256? GasBottleneck { get; set; }

    /// <summary>
    /// Fee-payer resolved at mempool admission for an EIP-8141 frame transaction; <c>null</c> until
    /// resolved or when it cannot be resolved natively.
    /// </summary>
    public Address? PayerAddress { get; set; }

    /// <summary>
    /// The maximum cost mempool admission priced this transaction at, released unchanged from
    /// <see cref="PayerAddress"/>'s exposure when the transaction leaves the pool. Recorded with no payer too,
    /// where it reserves nothing and only prices the sender's pending total.
    /// </summary>
    /// <remarks>
    /// Held rather than re-derived on release: the pool keeps a blob-carrying frame transaction as a light
    /// record with no frames, which cannot be priced, and the pricing spec moves with the head besides.
    /// </remarks>
    public UInt256? PayerExposure { get; set; }
}
