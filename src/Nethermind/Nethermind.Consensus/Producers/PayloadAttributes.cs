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

namespace Nethermind.Consensus.Producers;

public class PayloadAttributes
{
    public ulong Timestamp { get; set; }

    public Keccak PrevRandao { get; set; }

    public Address SuggestedFeeRecipient { get; set; }

    public IList<Withdrawal>? Withdrawals { get; set; }

    public Keccak? ParentBeaconBlockRoot { get; set; }
    /// <summary>Gets or sets the gas limit.</summary>
    /// <remarks>Used for MEV-Boost only.</remarks>
    public long? GasLimit { get; set; }

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
}

public enum PayloadAttributesValidationResult : byte { Success, InvalidParams, UnsupportedFork };

public static class PayloadAttributesExtensions
{
    public static string ComputePayloadId(this PayloadAttributes payloadAttributes, BlockHeader parentHeader)
    {
        bool hasWithdrawals = payloadAttributes.Withdrawals is not null;
        bool hasParentBeaconBlockRoot = payloadAttributes.ParentBeaconBlockRoot is not null;

        const int preambleLength = Keccak.Size + Keccak.Size + Keccak.Size + Address.ByteLength;
        Span<byte> inputSpan = stackalloc byte[preambleLength + (hasWithdrawals ? Keccak.Size : 0) + (hasParentBeaconBlockRoot ? Keccak.Size : 0)];

        parentHeader.Hash!.Bytes.CopyTo(inputSpan[..Keccak.Size]);
        BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(56, sizeof(UInt64)), payloadAttributes.Timestamp);
        payloadAttributes.PrevRandao.Bytes.CopyTo(inputSpan.Slice(64, Keccak.Size));
        payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, Address.ByteLength));

        if (hasWithdrawals)
        {
            var withdrawalsRootHash = payloadAttributes.Withdrawals.Count == 0
                ? PatriciaTree.EmptyTreeHash
                : new WithdrawalTrie(payloadAttributes.Withdrawals).RootHash;

            withdrawalsRootHash.Bytes.CopyTo(inputSpan[preambleLength..]);
        }

        if (hasParentBeaconBlockRoot)
        {
            payloadAttributes.ParentBeaconBlockRoot.Bytes.CopyTo(inputSpan[(preambleLength + (hasWithdrawals ? Keccak.Size : 0))..]);
        }

        ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);

        return inputHash.BytesAsSpan[..8].ToHexString(true);
    }

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
