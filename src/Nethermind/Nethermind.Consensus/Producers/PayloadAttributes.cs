// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Blockchain.ValidatorExit;

namespace Nethermind.Consensus.Producers;

public class PayloadAttributes
{
    public ulong Timestamp { get; set; }

    public Hash256 PrevRandao { get; set; }

    public Address SuggestedFeeRecipient { get; set; }

    public Withdrawal[]? Withdrawals { get; set; }

    public Hash256? ParentBeaconBlockRoot { get; set; }

    public virtual long? GetGasLimit() => null;

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation)
    {
        var sb = new StringBuilder($"{indentation}{nameof(PayloadAttributes)} {{")
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
        + (ParentBeaconBlockRoot is null ? 0 : Keccak.Size); // parent beacon block root

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

        return position;
    }

    private static PayloadAttributesValidationResult ValidateVersion(
        int apiVersion,
        int actualVersion,
        int timestampVersion,
        string methodName,
        [NotNullWhen(false)] out string? error)
    {
        // version calculated from parameters should match api version
        if (actualVersion != apiVersion)
        {
            // except of Shanghai api handling Paris fork
            if (apiVersion == EngineApiVersions.Shanghai && timestampVersion < apiVersion)
            {

                error = null;
                return PayloadAttributesValidationResult.Success;
            }

            error = $"{methodName}{apiVersion} expected";
            return actualVersion <= EngineApiVersions.Paris ? PayloadAttributesValidationResult.InvalidParams : PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        // timestamp should correspond to proper api version
        if (timestampVersion != apiVersion)
        {
            error = $"{methodName}{timestampVersion} expected";
            return timestampVersion <= EngineApiVersions.Paris ? PayloadAttributesValidationResult.InvalidParams : PayloadAttributesValidationResult.UnsupportedFork;
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
            timestampVersion: specProvider.GetSpec(ForkActivation.TimestampOnly(Timestamp))
                .ExpectedEngineSpecVersion(),
            "PayloadAttributesV",
            out error);
}

public enum PayloadAttributesValidationResult : byte { Success, InvalidParams, InvalidPayloadAttributes, UnsupportedFork };

public static class PayloadAttributesExtensions
{
    public static int GetVersion(this PayloadAttributes executionPayload) =>
        executionPayload switch
        {
            { ParentBeaconBlockRoot: not null, Withdrawals: not null } => EngineApiVersions.Cancun,
            { Withdrawals: not null } => EngineApiVersions.Shanghai,
            _ => EngineApiVersions.Paris
        };

    public static int ExpectedEngineSpecVersion(this IReleaseSpec spec) =>
        spec switch
        {
            { DepositsEnabled: true, ValidatorExitsEnabled: true } => EngineApiVersions.Prague,
            { IsEip4844Enabled: true } => EngineApiVersions.Cancun,
            { WithdrawalsEnabled: true } => EngineApiVersions.Shanghai,
            _ => EngineApiVersions.Paris
        };
}
