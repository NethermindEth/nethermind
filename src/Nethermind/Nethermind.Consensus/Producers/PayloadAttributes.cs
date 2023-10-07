// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    public Keccak PrevRandao { get; set; }

    public Address SuggestedFeeRecipient { get; set; }

    public IList<Withdrawal>? Withdrawals { get; set; }

    public Keccak? ParentBeaconBlockRoot { get; set; }

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
            sb.Append($", {nameof(Withdrawals)} count: {Withdrawals.Count}");
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

    [SkipLocalsInit]
    protected virtual string ComputePayloadId(BlockHeader parentHeader)
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
        ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);
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
            Keccak withdrawalsRootHash = Withdrawals.Count == 0
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
}

public enum PayloadAttributesValidationResult : byte { Success, InvalidParams, UnsupportedFork };

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
            { IsEip4844Enabled: true } => EngineApiVersions.Cancun,
            { WithdrawalsEnabled: true } => EngineApiVersions.Shanghai,
            _ => EngineApiVersions.Paris
        };

    public static PayloadAttributesValidationResult Validate(
       this PayloadAttributes payloadAttributes,
       ISpecProvider specProvider,
       int apiVersion,
       [NotNullWhen(false)] out string? error) =>
        Validate(
            apiVersion: apiVersion,
            actualVersion: payloadAttributes.GetVersion(),
            expectedVersion: specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp))
                                         .ExpectedEngineSpecVersion(),
            "PayloadAttributesV",
            out error);

    public static PayloadAttributesValidationResult Validate(
        int apiVersion,
        int actualVersion,
        int expectedVersion,
        string methodName,
        [NotNullWhen(false)] out string? error)
    {
        if (apiVersion >= EngineApiVersions.Cancun)
        {
            if (actualVersion == apiVersion && expectedVersion != apiVersion)
            {
                error = $"{methodName}{expectedVersion} expected";
                return PayloadAttributesValidationResult.UnsupportedFork;
            }
        }
        else if (apiVersion == EngineApiVersions.Shanghai)
        {
            if (actualVersion == apiVersion && expectedVersion >= EngineApiVersions.Cancun)
            {
                error = $"{methodName}{expectedVersion} expected";
                return PayloadAttributesValidationResult.UnsupportedFork;
            }
        }

        if (actualVersion == expectedVersion)
        {
            if (apiVersion >= EngineApiVersions.Cancun)
            {
                if (actualVersion == apiVersion)
                {
                    error = null;
                    return PayloadAttributesValidationResult.Success;
                }
            }
            else
            {
                if (apiVersion >= actualVersion)
                {
                    error = null;
                    return PayloadAttributesValidationResult.Success;
                }
            }
        }

        error = $"{methodName}{expectedVersion} expected";
        return PayloadAttributesValidationResult.InvalidParams;
    }
}
