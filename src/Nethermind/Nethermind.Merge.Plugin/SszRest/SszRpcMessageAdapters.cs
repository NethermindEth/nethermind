// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest;

internal interface ISszRpcRequest<TSelf>
{
    static abstract object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body);
}

internal interface ISszRpcResponse<TSelf, TDomain>
{
    static abstract TSelf FromDomain(TDomain value);
}

internal readonly struct PayloadIdRequest : ISszRpcRequest<PayloadIdRequest>
{
    private const int PayloadIdHexLength = 16;
    private const int PayloadIdByteLength = 8;

    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body) =>
        [ParsePayloadId(extra.Span)];

    private static byte[] ParsePayloadId(ReadOnlySpan<char> extra)
    {
        if (extra.Length == 0)
            throw new SszRequestValidationException(StatusCodes.Status400BadRequest, "Missing payload ID");

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != PayloadIdHexLength)
            throw new SszRequestValidationException(
                StatusCodes.Status400BadRequest,
                $"Invalid payload ID: '{extra}' (expected {PayloadIdHexLength} hex chars)");

        Span<byte> stack = stackalloc byte[PayloadIdByteLength];
        if (Convert.FromHexString(hex, stack, out _, out _) != OperationStatus.Done)
            throw new SszRequestValidationException(StatusCodes.Status400BadRequest, $"Invalid payload ID: '{extra}'");

        return stack.ToArray();
    }
}

internal sealed class SszRequestValidationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public partial struct ExchangeCapabilitiesRequestWire : ISszRpcRequest<ExchangeCapabilitiesRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ExchangeCapabilitiesRequestWire wire);
        if (wire.Capabilities is null) return [Array.Empty<string>()];

        string[] result = new string[wire.Capabilities.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Encoding.UTF8.GetString(wire.Capabilities[i].Name ?? []);

        return [result];
    }
}

public partial struct GetClientVersionRequestWire : ISszRpcRequest<GetClientVersionRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetClientVersionRequestWire wire);
        ClientVersionWire cl = wire.ClientVersion;
        ClientVersionV1 clientVersion = new()
        {
            Code = Encoding.UTF8.GetString(cl.Code ?? []),
            Name = Encoding.UTF8.GetString(cl.Name ?? []),
            Version = Encoding.UTF8.GetString(cl.Version ?? []),
            Commit = cl.Commit is { Length: 4 } c ? Convert.ToHexString(c) : new string('0', 8),
        };

        return [clientVersion];
    }
}

public partial struct ForkchoiceUpdatedV1RequestWire : ISszRpcRequest<ForkchoiceUpdatedV1RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV1RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return [SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs];
    }
}

public partial struct ForkchoiceUpdatedV2RequestWire : ISszRpcRequest<ForkchoiceUpdatedV2RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV2RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return [SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs];
    }
}

public partial struct ForkchoiceUpdatedV3RequestWire : ISszRpcRequest<ForkchoiceUpdatedV3RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV3RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return [SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs];
    }
}

public partial struct ForkchoiceUpdatedRequestWire : ISszRpcRequest<ForkchoiceUpdatedRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedRequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return [SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs];
    }
}

public partial struct NewPayloadV1RequestWire : ISszRpcRequest<NewPayloadV1RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV1RequestWire wire);
        return [wire.ExecutionPayload.Unwrap()];
    }
}

public partial struct NewPayloadV2RequestWire : ISszRpcRequest<NewPayloadV2RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV2RequestWire wire);
        return [wire.ExecutionPayload.Unwrap()];
    }
}

public partial struct NewPayloadV3RequestWire : ISszRpcRequest<NewPayloadV3RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV3RequestWire wire);
        return [wire.ExecutionPayload.Unwrap(), wire.ExpectedBlobVersionedHashes.ToBytesArrays(), wire.ParentBeaconBlockRoot];
    }
}

public partial struct NewPayloadV4RequestWire : ISszRpcRequest<NewPayloadV4RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV4RequestWire wire);
        return [
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ];
    }
}

public partial struct NewPayloadV5RequestWire : ISszRpcRequest<NewPayloadV5RequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV5RequestWire wire);
        return [
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ];
    }
}

public partial struct GetBlobsRequestWire : ISszRpcRequest<GetBlobsRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetBlobsRequestWire wire);
        if (wire.VersionedHashes is null) return [Array.Empty<byte[]>()];

        byte[][] result = new byte[wire.VersionedHashes.Length][];
        for (int i = 0; i < result.Length; i++)
            result[i] = wire.VersionedHashes[i].Bytes.ToArray();

        return [result];
    }
}

public partial struct GetPayloadBodiesByHashRequestWire : ISszRpcRequest<GetPayloadBodiesByHashRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetPayloadBodiesByHashRequestWire wire);
        return [wire.BlockHashes ?? []];
    }
}

public partial struct GetPayloadBodiesByRangeRequestWire : ISszRpcRequest<GetPayloadBodiesByRangeRequestWire>
{
    public static object?[] DecodeArguments(ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetPayloadBodiesByRangeRequestWire wire);
        return [(long)wire.Start, (long)wire.Count];
    }
}

public partial struct PayloadStatusWire : ISszRpcResponse<PayloadStatusWire, PayloadStatusV1>
{
    public static PayloadStatusWire FromDomain(PayloadStatusV1 value) => SszRpcMessageAdapter.BuildPayloadStatusWire(value);
}

public partial struct ForkchoiceUpdatedResponseWire : ISszRpcResponse<ForkchoiceUpdatedResponseWire, ForkchoiceUpdatedV1Result>
{
    public static ForkchoiceUpdatedResponseWire FromDomain(ForkchoiceUpdatedV1Result value)
    {
        SszBytes8[]? pidList = null;
        if (value.PayloadId is not null)
        {
            ReadOnlySpan<char> hex = value.PayloadId.AsSpan();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (hex.Length != 16)
                throw new InvalidOperationException($"Invalid payload id '{value.PayloadId}': expected 16 hex chars, got {hex.Length}");

            Span<byte> stack = stackalloc byte[8];
            Bytes.FromHexString(hex, stack);
            pidList = [SszBytes8.FromSpan(stack)];
        }

        return new ForkchoiceUpdatedResponseWire
        {
            PayloadStatus = SszRpcMessageAdapter.BuildPayloadStatusWire(value.PayloadStatus),
            PayloadId = pidList ?? []
        };
    }
}

public partial class SszExecutionPayloadV1 : ISszRpcResponse<SszExecutionPayloadV1, ExecutionPayload>
{
    public static SszExecutionPayloadV1 FromDomain(ExecutionPayload value) => new(value);
}

public partial struct GetPayloadResponseV2Wire : ISszRpcResponse<GetPayloadResponseV2Wire, GetPayloadV2Result?>
{
    public static GetPayloadResponseV2Wire FromDomain(GetPayloadV2Result? value) => new()
    {
        ExecutionPayload = new SszExecutionPayload(value!.ExecutionPayload),
        BlockValue = value.BlockValue
    };
}

public partial struct GetPayloadResponseV3Wire : ISszRpcResponse<GetPayloadResponseV3Wire, GetPayloadV3Result?>
{
    public static GetPayloadResponseV3Wire FromDomain(GetPayloadV3Result? value) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)value!.ExecutionPayload),
        BlockValue = value.BlockValue,
        BlobsBundle = value.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = value.ShouldOverrideBuilder
    };
}

public partial struct GetPayloadResponseV4Wire : ISszRpcResponse<GetPayloadResponseV4Wire, GetPayloadV4Result?>
{
    public static GetPayloadResponseV4Wire FromDomain(GetPayloadV4Result? value) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)value!.ExecutionPayload),
        BlockValue = value.BlockValue,
        BlobsBundle = value.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = value.ShouldOverrideBuilder,
        ExecutionRequests = value.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetPayloadResponseV5Wire : ISszRpcResponse<GetPayloadResponseV5Wire, GetPayloadV5Result?>
{
    public static GetPayloadResponseV5Wire FromDomain(GetPayloadV5Result? value) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)value!.ExecutionPayload),
        BlockValue = value.BlockValue,
        BlobsBundle = value.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = value.ShouldOverrideBuilder,
        ExecutionRequests = value.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetPayloadResponseV6Wire : ISszRpcResponse<GetPayloadResponseV6Wire, GetPayloadV6Result?>
{
    public static GetPayloadResponseV6Wire FromDomain(GetPayloadV6Result? value) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV4((ExecutionPayloadV4)value!.ExecutionPayload),
        BlockValue = value.BlockValue,
        BlobsBundle = value.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = value.ShouldOverrideBuilder,
        ExecutionRequests = value.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetBlobsV1ResponseWire : ISszRpcResponse<GetBlobsV1ResponseWire, IReadOnlyList<BlobAndProofV1?>>
{
    public static GetBlobsV1ResponseWire FromDomain(IReadOnlyList<BlobAndProofV1?> value)
    {
        int count = value.Count;
        int filled = 0;
        for (int i = 0; i < count; i++) if (value[i] is not null) filled++;

        BlobAndProofV1Wire[] arr = new BlobAndProofV1Wire[filled];
        int j = 0;
        for (int i = 0; i < count; i++)
            if (value[i] is { } blobAndProof)
                arr[j++] = new BlobAndProofV1Wire { Blob = blobAndProof.Blob, Proof = blobAndProof.Proof };

        return new GetBlobsV1ResponseWire { BlobsAndProofs = arr };
    }
}

public partial struct GetBlobsV2ResponseWire : ISszRpcResponse<GetBlobsV2ResponseWire, IReadOnlyList<BlobAndProofV2?>?>
{
    public static GetBlobsV2ResponseWire FromDomain(IReadOnlyList<BlobAndProofV2?>? value)
    {
        int count = value!.Count;
        int filled = 0;
        for (int i = 0; i < count; i++) if (value[i] is not null) filled++;

        BlobAndProofV2Wire[] arr = new BlobAndProofV2Wire[filled];
        int j = 0;
        for (int i = 0; i < count; i++)
            if (value[i] is { } blobAndProof)
                arr[j++] = new BlobAndProofV2Wire { Blob = blobAndProof.Blob, Proofs = blobAndProof.Proofs.ToKzgWire() };

        return new GetBlobsV2ResponseWire { BlobsAndProofs = arr };
    }
}

public partial struct GetBlobsV3ResponseWire : ISszRpcResponse<GetBlobsV3ResponseWire, IReadOnlyList<BlobAndProofV2?>?>
{
    public static GetBlobsV3ResponseWire FromDomain(IReadOnlyList<BlobAndProofV2?>? value)
    {
        int count = value!.Count;
        NullableBlobAndProofV2Wire[] arr = new NullableBlobAndProofV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? blobAndProof = value[i];
            arr[i] = blobAndProof is null
                ? new NullableBlobAndProofV2Wire { BlobAndProof = [] }
                : new NullableBlobAndProofV2Wire { BlobAndProof = [new BlobAndProofV2Wire { Blob = blobAndProof.Blob, Proofs = blobAndProof.Proofs.ToKzgWire() }] };
        }

        return new GetBlobsV3ResponseWire { BlobsAndProofs = arr };
    }
}

public partial struct PayloadBodiesV1ResponseWire : ISszRpcResponse<PayloadBodiesV1ResponseWire, IReadOnlyList<ExecutionPayloadBodyV1Result?>>
{
    public static PayloadBodiesV1ResponseWire FromDomain(IReadOnlyList<ExecutionPayloadBodyV1Result?> value)
    {
        int count = value.Count;
        NullablePayloadBodyV1Wire[] arr = new NullablePayloadBodyV1Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV1Result? body = value[i];
            arr[i] = new NullablePayloadBodyV1Wire { Body = body is null ? [] : [body.ToBodyWire()] };
        }

        return new PayloadBodiesV1ResponseWire { PayloadBodies = arr };
    }
}

public partial struct PayloadBodiesV2ResponseWire : ISszRpcResponse<PayloadBodiesV2ResponseWire, IReadOnlyList<ExecutionPayloadBodyV2Result?>>
{
    public static PayloadBodiesV2ResponseWire FromDomain(IReadOnlyList<ExecutionPayloadBodyV2Result?> value)
    {
        int count = value.Count;
        NullablePayloadBodyV2Wire[] arr = new NullablePayloadBodyV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV2Result? body = value[i];
            arr[i] = new NullablePayloadBodyV2Wire { Body = body is null ? [] : [body.ToBodyWire()] };
        }

        return new PayloadBodiesV2ResponseWire { PayloadBodies = arr };
    }
}

public partial struct ExchangeCapabilitiesResponseWire : ISszRpcResponse<ExchangeCapabilitiesResponseWire, IReadOnlyList<string>>
{
    public static ExchangeCapabilitiesResponseWire FromDomain(IReadOnlyList<string> value)
    {
        int count = value.Count;
        SszCapabilityName[] arr = new SszCapabilityName[count];
        for (int i = 0; i < count; i++)
            arr[i] = new SszCapabilityName { Name = Encoding.UTF8.GetBytes(value[i]) };

        return new ExchangeCapabilitiesResponseWire { Capabilities = arr };
    }
}

public partial struct GetClientVersionResponseWire : ISszRpcResponse<GetClientVersionResponseWire, ClientVersionV1[]>
{
    public static GetClientVersionResponseWire FromDomain(ClientVersionV1[] value)
    {
        ClientVersionWire[] wireVersions = new ClientVersionWire[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            string commitHex = value[i].Commit ?? string.Empty;
            byte[] commit = commitHex.Length >= 8
                ? Convert.FromHexString(commitHex[..8])
                : new byte[4];
            wireVersions[i] = new ClientVersionWire
            {
                Code = Encoding.UTF8.GetBytes(value[i].Code ?? string.Empty),
                Name = Encoding.UTF8.GetBytes(value[i].Name ?? string.Empty),
                Version = Encoding.UTF8.GetBytes(value[i].Version ?? string.Empty),
                Commit = commit
            };
        }

        return new GetClientVersionResponseWire { Versions = wireVersions };
    }
}

file static class SszRpcMessageAdapter
{
    public static ForkchoiceStateV1 ForkchoiceStateV1FromWire(ForkchoiceStateWire wire) => new(
        headBlockHash: wire.HeadBlockHash,
        finalizedBlockHash: wire.FinalizedBlockHash,
        safeBlockHash: wire.SafeBlockHash);

    public static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV1Wire payloadAttributes) =>
        BuildPayloadAttributes(payloadAttributes.Timestamp, payloadAttributes.PrevRandao, payloadAttributes.SuggestedFeeRecipient);

    public static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV2Wire payloadAttributes) =>
        BuildPayloadAttributes(payloadAttributes.Timestamp, payloadAttributes.PrevRandao, payloadAttributes.SuggestedFeeRecipient,
            withdrawals: payloadAttributes.Withdrawals.ToDomain());

    public static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV3Wire payloadAttributes) =>
        BuildPayloadAttributes(payloadAttributes.Timestamp, payloadAttributes.PrevRandao, payloadAttributes.SuggestedFeeRecipient,
            withdrawals: payloadAttributes.Withdrawals.ToDomain(),
            parentBeaconBlockRoot: payloadAttributes.ParentBeaconBlockRoot);

    public static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesWire payloadAttributes) =>
        BuildPayloadAttributes(payloadAttributes.Timestamp, payloadAttributes.PrevRandao, payloadAttributes.SuggestedFeeRecipient,
            withdrawals: payloadAttributes.Withdrawals.ToDomain(),
            parentBeaconBlockRoot: payloadAttributes.ParentBeaconBlockRoot,
            slotNumber: payloadAttributes.SlotNumber);

    public static PayloadStatusWire BuildPayloadStatusWire(PayloadStatusV1 payloadStatus)
    {
        const int MaxErrorBytes = 1024;
        byte[] errorBytes = payloadStatus.ValidationError is not null
            ? Encoding.UTF8.GetBytes(payloadStatus.ValidationError)
            : [];
        if (errorBytes.Length > MaxErrorBytes)
            errorBytes = errorBytes[..MaxErrorBytes];

        return new PayloadStatusWire
        {
            Status = EngineStatusToSsz(payloadStatus.Status),
            LatestValidHash = payloadStatus.LatestValidHash is not null ? [payloadStatus.LatestValidHash] : [],
            ValidationError = errorBytes
        };
    }

    private static PayloadAttributes BuildPayloadAttributes(
        ulong timestamp,
        Hash256 prevRandao,
        Address suggestedFeeRecipient,
        Withdrawal[]? withdrawals = null,
        Hash256? parentBeaconBlockRoot = null,
        ulong? slotNumber = null) => new()
        {
            Timestamp = timestamp,
            PrevRandao = prevRandao,
            SuggestedFeeRecipient = suggestedFeeRecipient,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = parentBeaconBlockRoot,
            SlotNumber = slotNumber
        };

    private static byte EngineStatusToSsz(string status) => status switch
    {
        PayloadStatus.Valid => 0,
        PayloadStatus.Invalid => 1,
        PayloadStatus.Syncing => 2,
        PayloadStatus.Accepted => 3,
        _ => throw new InvalidOperationException($"Unknown payload status '{status}': cannot map to SSZ wire byte")
    };
}
