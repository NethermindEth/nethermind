// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class TaikoPayloadAttributes : PayloadAttributes
{
    // Tags the payload id with the engine_*V2 surface Taiko exposes; matches alethia-reth's
    // PAYLOAD_ID_VERSION_V2.
    private const byte PayloadIdVersionV2 = 0x02;

    public UInt256 BaseFeePerGas { get; set; }
    public BlockMetadata? BlockMetadata { get; set; }
    public L1Origin? L1Origin { get; set; }

    private string? _taikoPayloadId;

    public override long GetGasLimit(BlockHeader parent, IGasLimitCalculator gasLimitCalculator) => BlockMetadata!.GasLimit;

    /// <summary>
    /// Computes the Taiko-canonical payload id so it matches the <c>buildPayloadArgsId</c> stored
    /// in the L1 origin and the value produced by alethia-reth / taiko-geth.
    /// </summary>
    /// <remarks>
    /// The base implementation keccak-hashes a different field set, so it never agrees with the
    /// other Taiko execution clients. This mirrors alethia-reth's <c>payload_id_taiko</c>: a
    /// SHA-256 digest over parent hash, big-endian timestamp, prev randao, fee recipient,
    /// RLP-encoded withdrawals, optional parent beacon block root, <c>keccak256(txList)</c>, and
    /// extra data, with the first byte overwritten by the version tag.
    /// </remarks>
    public override string GetPayloadId(BlockHeader parentHeader) =>
        _taikoPayloadId ??= ComputeTaikoPayloadId(parentHeader);

    private string ComputeTaikoPayloadId(BlockHeader parentHeader)
    {
        BlockMetadata meta = BlockMetadata
            ?? throw new InvalidOperationException($"{nameof(BlockMetadata)} is required to compute the payload id.");

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hasher.AppendData(parentHeader.Hash!.Bytes);

        Span<byte> timestamp = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, Timestamp);
        hasher.AppendData(timestamp);

        hasher.AppendData((PrevRandao ?? Keccak.Zero).Bytes);
        hasher.AppendData((SuggestedFeeRecipient ?? Address.Zero).Bytes);

        if (Withdrawals is not null)
        {
            AppendWithdrawals(hasher, Withdrawals);
        }

        if (ParentBeaconBlockRoot is not null)
        {
            hasher.AppendData(ParentBeaconBlockRoot.Bytes);
        }

        hasher.AppendData(ValueKeccak.Compute(meta.TxList).Bytes);
        hasher.AppendData(meta.ExtraData);

        Span<byte> digest = stackalloc byte[32];
        hasher.GetHashAndReset(digest);
        digest[0] = PayloadIdVersionV2;
        return digest[..8].ToHexString(true);
    }

    // RLP-encodes the withdrawals as a list (empty list -> 0xc0), matching alloy's
    // Withdrawals::encode, and feeds the bytes into the running digest.
    private static void AppendWithdrawals(IncrementalHash hasher, Withdrawal[] withdrawals)
    {
        WithdrawalDecoder codec = new();
        int contentLength = 0;
        foreach (Withdrawal withdrawal in withdrawals)
        {
            contentLength += codec.GetLength(withdrawal, RlpBehaviors.None);
        }

        using ArrayPoolSpan<byte> bytes = new(Rlp.LengthOfSequence(contentLength));
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        foreach (Withdrawal withdrawal in withdrawals)
        {
            codec.Encode(ref writer, withdrawal);
        }

        hasher.AppendData(bytes);
    }

    public override PayloadAttributesValidationResult Validate(ISpecProvider specProvider, int fcuVersion,
        [NotNullWhen(false)] out string? error)
    {
        if (L1Origin is null)
        {
            error = "L1Origin is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        if (BlockMetadata is null)
        {
            error = "BlockMetadata is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        if (BlockMetadata.Beneficiary is null)
        {
            error = "BlockMetadata.Beneficiary is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.MixHash is null)
        {
            error = "BlockMetadata.MixHash is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.TxList is null)
        {
            error = "BlockMetadata.TxList is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.ExtraData is null)
        {
            error = "BlockMetadata.ExtraData is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        // Taiko always uses V2 engine API payloads regardless of the active EVM fork
        // (Cancun/Prague/Osaka). Skip the base fork-version check which would reject
        // V2 attributes once EIP-4844 is active and demand V3.
        error = null;
        return PayloadAttributesValidationResult.Success;
    }

}
