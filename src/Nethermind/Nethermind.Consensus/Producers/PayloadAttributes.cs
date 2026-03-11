// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Consensus.Producers;

public class PayloadAttributes
{
    public ulong Timestamp { get; set; }

    public Hash256 PrevRandao { get; set; }

    public Address SuggestedFeeRecipient { get; set; }

    public Withdrawal[]? Withdrawals { get; set; }

    public Hash256? ParentBeaconBlockRoot { get; set; }

    public ulong? SlotNumber { get; set; }

    public virtual long? GetGasLimit() => null;

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation)
    {
        StringBuilder sb = new StringBuilder($"{indentation}{nameof(PayloadAttributes)} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}");

        if (Withdrawals is not null)
        {
            sb.Append($", {nameof(Withdrawals)} count: {Withdrawals.Length}");
        }

        if (ParentBeaconBlockRoot is not null)
        {
            sb.Append($", {nameof(ParentBeaconBlockRoot)} : {ParentBeaconBlockRoot}");
        }

        if (SlotNumber is not null)
        {
            sb.Append($", {nameof(SlotNumber)}: {SlotNumber}");
        }

        sb.Append('}');

        return sb.ToString();
    }


    private string? _payloadId;

    public string GetPayloadId(BlockHeader parentHeader) => _payloadId ??= ComputePayloadId(parentHeader);

    private string ComputePayloadId(BlockHeader parentHeader)
    {
        int size = ComputePayloadIdMembersSize();
        Span<byte> inputSpan = stackalloc byte[size];
        WritePayloadIdMembers(parentHeader, inputSpan);
        return ComputePayloadId(inputSpan);
    }

    protected virtual int ComputePayloadIdMembersSize() =>
        Keccak.Size // parent hash
        + sizeof(ulong) // timestamp
        + Keccak.Size // prev randao
        + Address.Size // suggested fee recipient
        + (Withdrawals is null ? 0 : Keccak.Size) // withdrawals root hash
        + (ParentBeaconBlockRoot is null ? 0 : Keccak.Size) // parent beacon block root
        + (SlotNumber is null ? 0 : sizeof(ulong)); // slot number

    protected static string ComputePayloadId(Span<byte> inputSpan)
    {
        ValueHash256 inputHash = ValueKeccak.Compute(inputSpan);
        return inputHash.BytesAsSpan[..8].ToHexString(true);
    }

    protected virtual int WritePayloadIdMembers(BlockHeader parentHeader, Span<byte> inputSpan)
    {
        int position = 0;

        parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(position, Keccak.Size));
        position += Keccak.Size;

        BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(position, sizeof(ulong)), Timestamp);
        position += sizeof(ulong);

        PrevRandao.Bytes.CopyTo(inputSpan.Slice(position, Keccak.Size));
        position += Keccak.Size;

        SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(position, Address.Size));
        position += Address.Size;

        if (Withdrawals is not null)
        {
            Hash256 withdrawalsRootHash = Withdrawals.Length == 0
                ? PatriciaTree.EmptyTreeHash
                : new WithdrawalTrie(Withdrawals).RootHash;
            withdrawalsRootHash.Bytes.CopyTo(inputSpan.Slice(position, Keccak.Size));
            position += Keccak.Size;
        }

        if (ParentBeaconBlockRoot is not null)
        {
            ParentBeaconBlockRoot.Bytes.CopyTo(inputSpan.Slice(position, Keccak.Size));
            position += Keccak.Size;
        }

        if (SlotNumber is not null)
        {
            BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(position, sizeof(ulong)), SlotNumber.Value);
            position += sizeof(ulong);
        }

        return position;
    }

    /// <summary>
    /// The matrix of valid (FCU version, timestamp fork) combinations.
    /// Each entry maps to the PayloadAttributes version the caller must provide.
    /// </summary>
    /// <returns>
    /// The expected PayloadAttributes version for the combination,
    /// or null if this FCU version doesn't support the given fork
    /// (e.g. FCUv3 at a Shanghai timestamp, or FCUv2 at a Cancun timestamp).
    /// </returns>
    private static int? ExpectedPayloadVersion(int apiVersion, int timestampVersion) =>
        (EngineApiVersions.FcuVersion(apiVersion), timestampVersion) switch
        {
            (EngineApiVersions.Fcu.V1, PayloadAttributesVersions.Paris) => PayloadAttributesVersions.Paris,
            (EngineApiVersions.Fcu.V2, PayloadAttributesVersions.Paris or PayloadAttributesVersions.Shanghai) => timestampVersion,
            (EngineApiVersions.Fcu.V3, PayloadAttributesVersions.Cancun) => PayloadAttributesVersions.Cancun,
            (EngineApiVersions.Fcu.V4, PayloadAttributesVersions.Amsterdam) => PayloadAttributesVersions.Amsterdam,
            _ => null
        };

    /// <summary>
    /// Validates that the payload attributes version is consistent with the engine API version and the fork indicated by the timestamp.
    /// </summary>
    /// <param name="apiVersion">The engine API version of the called method (e.g. <see cref="EngineApiVersions.Shanghai"/> for FCUv2).</param>
    /// <param name="actualVersion">The payload attributes version inferred from the fields present in the request (see <see cref="PayloadAttributesExtensions.GetVersion"/>).</param>
    /// <param name="timestampVersion">The payload attributes version expected for the fork active at the requested timestamp (see <see cref="PayloadAttributesExtensions.ExpectedPayloadAttributesVersion"/>).</param>
    /// <param name="methodName">Prefix for error messages (e.g. "PayloadAttributesV").</param>
    /// <param name="error">Set to a descriptive message on failure; null on success.</param>
    /// <returns>
    /// <see cref="PayloadAttributesValidationResult.Success"/> when the (FCU version, fork, attributes) combination is valid;
    /// <see cref="PayloadAttributesValidationResult.UnsupportedFork"/> when the FCU version doesn't support the timestamp's fork (post-Paris only, since the error code was introduced after Paris);
    /// <see cref="PayloadAttributesValidationResult.InvalidPayloadAttributes"/> when the attributes structure doesn't match what the fork expects (e.g. missing withdrawals at Shanghai),
    /// or when an unsupported Paris-era combination is encountered (falls back from UnsupportedFork since that error code didn't exist at Paris).
    /// </returns>
    private static PayloadAttributesValidationResult ValidateVersion(
        int apiVersion,
        int actualVersion,
        int timestampVersion,
        string methodName,
        [NotNullWhen(false)] out string? error)
    {
        // Look up the matrix of PayloadVersion that corresponds to the FCU version.
        int? expectedPayloadVersion = ExpectedPayloadVersion(apiVersion, timestampVersion);

        // If null — this FCU version doesn't support this fork at all.
        if (expectedPayloadVersion is null)
        {
            error = $"{methodName}{timestampVersion} expected";
            return timestampVersion >= PayloadAttributesVersions.Shanghai
                ? PayloadAttributesValidationResult.UnsupportedFork // error code added in Shanghai
                : PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        // Compare the expected version to what the caller actually sent.
        // If mismatch → InvalidPayloadAttributes (-38003): "wrong attributes structure for this fork (e.g., missing withdrawals at Shanghai, or extra withdrawals before Shanghai)
        if (actualVersion != expectedPayloadVersion)
        {
            error = $"{methodName}{expectedPayloadVersion} expected";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        error = null;
        return PayloadAttributesValidationResult.Success;
    }

    public virtual PayloadAttributesValidationResult Validate(
        ISpecProvider specProvider,
        int apiVersion,
        [NotNullWhen(false)] out string? error) =>
        ValidateVersion(
            apiVersion: apiVersion,
            actualVersion: this.GetVersion(),
            timestampVersion: specProvider.GetSpec(ForkActivation.TimestampOnly(Timestamp)).ExpectedPayloadAttributesVersion(),
            "PayloadAttributesV",
            out error);
}

public enum PayloadAttributesValidationResult : byte { Success, InvalidParams, InvalidPayloadAttributes, UnsupportedFork };

public static class PayloadAttributesExtensions
{
    public static int GetVersion(this PayloadAttributes executionPayload) =>
        executionPayload switch
        {
            { SlotNumber: not null } => PayloadAttributesVersions.Amsterdam,
            { ParentBeaconBlockRoot: not null, Withdrawals: not null } => PayloadAttributesVersions.Cancun,
            { Withdrawals: not null } => PayloadAttributesVersions.Shanghai,
            _ => PayloadAttributesVersions.Paris
        };

    public static int ExpectedPayloadAttributesVersion(this IReleaseSpec spec) =>
        spec switch
        {
            { IsEip7843Enabled: true } => PayloadAttributesVersions.Amsterdam,
            { IsEip4844Enabled: true } => PayloadAttributesVersions.Cancun,
            { WithdrawalsEnabled: true } => PayloadAttributesVersions.Shanghai,
            _ => PayloadAttributesVersions.Paris
        };
}

public static class PayloadAttributesVersions
{
    public const int Paris = 1;
    public const int Shanghai = 2;
    public const int Cancun = 3;
    public const int Amsterdam = 4;
}
