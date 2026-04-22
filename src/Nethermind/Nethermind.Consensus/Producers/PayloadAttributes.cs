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

    public Hash256? PrevRandao { get; set; }

    public Address? SuggestedFeeRecipient { get; set; }

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

        (PrevRandao ?? Keccak.Zero).Bytes.CopyTo(inputSpan.Slice(position, Keccak.Size));
        position += Keccak.Size;

        (SuggestedFeeRecipient ?? Address.Zero).Bytes.CopyTo(inputSpan.Slice(position, Address.Size));
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
    /// Whether this FCU version supports the given fork (identified by its payload attributes version).
    /// General rule: FCU version must match the payload attributes version.
    /// </summary>
    private static bool IsSupportedFcuForkCombination(int fcuVersion, int payloadVersion) =>
        (fcuVersion, payloadVersion) switch
        {
            // Exception: FCUv2 also accepts Paris (V1) attributes for backward compatibility.
            (EngineApiVersions.Fcu.V2, PayloadAttributesVersions.V1) => true,
            _ => fcuVersion == payloadVersion
        };

    /// <summary>
    /// Validates that the payload attributes version is consistent with the FCU version and the fork indicated by the timestamp.
    /// </summary>
    /// <returns>
    /// <see cref="PayloadAttributesValidationResult.UnsupportedFork"/> — FCU version doesn't support this fork (post-Paris only);
    /// <see cref="PayloadAttributesValidationResult.InvalidPayloadAttributes"/> — attributes structure doesn't match the fork;
    /// <see cref="PayloadAttributesValidationResult.Success"/> — valid combination.
    /// </returns>
    private static PayloadAttributesValidationResult ValidateVersion(
        int fcuVersion,
        int actualVersion,
        int timestampVersion,
        string methodName,
        [NotNullWhen(false)] out string? error)
    {
        // This FCU version doesn't support this fork at all (e.g. V3 attrs sent to FCUv2).
        if (!IsSupportedFcuForkCombination(fcuVersion, actualVersion))
        {
            error = $"{methodName}{fcuVersion} expected";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        // Attributes structure doesn't match what the fork expects (e.g. V3 attrs sent to when FCUv3 not yet activated in spec).
        if (actualVersion != timestampVersion)
        {
            error = $"{methodName}{timestampVersion} expected";
            // FCU also doesn't support this fork → UnsupportedFork (post-Paris only)
            return fcuVersion != timestampVersion && timestampVersion >= PayloadAttributesVersions.V2
                ? PayloadAttributesValidationResult.UnsupportedFork
                : PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        error = null;
        return PayloadAttributesValidationResult.Success;
    }

    public virtual PayloadAttributesValidationResult Validate(
        ISpecProvider specProvider,
        int fcuVersion,
        [NotNullWhen(false)] out string? error)
    {
        int actualVersion = this.GetVersion();
        PayloadAttributesValidationResult result = ValidateVersion(
            fcuVersion,
            actualVersion,
            timestampVersion: specProvider.GetSpec(ForkActivation.TimestampOnly(Timestamp)).ExpectedPayloadAttributesVersion(),
            "PayloadAttributesV",
            out error);

        if (result == PayloadAttributesValidationResult.Success)
        {
            error = ValidateFields(actualVersion);
            result = error is null
                ? PayloadAttributesValidationResult.Success
                : PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        return result;
    }

    private string? ValidateFields(int actualVersion)
    {
        if (Timestamp == 0) return $"{nameof(Timestamp)} must be provided";
        if (PrevRandao is null) return $"{nameof(PrevRandao)} must be provided";
        if (SuggestedFeeRecipient is null) return $"{nameof(SuggestedFeeRecipient)} must be provided";

        return actualVersion switch
        {
            >= PayloadAttributesVersions.V2 when Withdrawals is null => $"{nameof(Withdrawals)} must be provided",
            >= PayloadAttributesVersions.V3 when ParentBeaconBlockRoot is null => $"{nameof(ParentBeaconBlockRoot)} must be provided",
            >= PayloadAttributesVersions.V4 when SlotNumber is null => $"{nameof(SlotNumber)} must be provided",
            _ => null
        };
    }
}

public enum PayloadAttributesValidationResult : byte { Success, InvalidPayloadAttributes, UnsupportedFork };

public static class PayloadAttributesExtensions
{
    public static int GetVersion(this PayloadAttributes executionPayload) =>
        executionPayload switch
        {
            { SlotNumber: not null } => PayloadAttributesVersions.V4,
            { ParentBeaconBlockRoot: not null } => PayloadAttributesVersions.V3,
            { Withdrawals: not null } => PayloadAttributesVersions.V2,
            _ => PayloadAttributesVersions.V1
        };

    public static int ExpectedPayloadAttributesVersion(this IReleaseSpec spec) =>
        spec switch
        {
            { IsEip7843Enabled: true } => PayloadAttributesVersions.V4,
            { IsEip4844Enabled: true } => PayloadAttributesVersions.V3,
            { WithdrawalsEnabled: true } => PayloadAttributesVersions.V2,
            _ => PayloadAttributesVersions.V1
        };
}

public static class PayloadAttributesVersions
{
    public const int V1 = 1; // Paris
    public const int V2 = 2; // Shanghai
    public const int V3 = 3; // Cancun/Prague/Osaka
    public const int V4 = 4; // Amsterdam
}
