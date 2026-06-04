// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest;

internal interface ISszRpcRequest<TSelf>
{
}

internal interface ISszRpcRequest<TSelf, TArguments> : ISszRpcRequest<TSelf>
{
    static abstract Result<TArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body);
}

internal interface ISszForkchoiceArguments
{
    ulong? Timestamp { get; }
}

public readonly record struct ForkchoiceUpdatedArguments(
    ForkchoiceStateV1 ForkchoiceState,
    PayloadAttributes? PayloadAttributes,
    BitArray? CustodyColumns = null,
    ulong? Timestamp = null) : ISszForkchoiceArguments;

public readonly record struct NewPayloadV3Arguments(
    ExecutionPayloadV3 ExecutionPayload,
    Hash256?[] BlobVersionedHashes,
    Hash256? ParentBeaconBlockRoot);

public readonly record struct NewPayloadV4Arguments(
    ExecutionPayloadV3 ExecutionPayload,
    Hash256?[] BlobVersionedHashes,
    Hash256? ParentBeaconBlockRoot,
    byte[][]? ExecutionRequests);

public readonly record struct NewPayloadV5Arguments(
    ExecutionPayloadV4 ExecutionPayload,
    Hash256?[] BlobVersionedHashes,
    Hash256? ParentBeaconBlockRoot,
    byte[][]? ExecutionRequests);

public readonly record struct PayloadBodiesByRangeArguments(long Start, long Count);

public readonly record struct GetBlobsV4Arguments(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);

internal readonly struct PayloadIdRequest : ISszRpcRequest<PayloadIdRequest, byte[]>
{
    private const int PayloadIdHexLength = 16;
    private const int PayloadIdByteLength = 8;

    public static Result<byte[]> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body) =>
        ParsePayloadId(extra.Span);

    private static Result<byte[]> ParsePayloadId(ReadOnlySpan<char> extra)
    {
        if (extra.Length == 0)
            return Result<byte[]>.Fail("Missing payload ID", []);

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != PayloadIdHexLength)
            return Result<byte[]>.Fail($"Invalid payload ID: '{extra}' (expected {PayloadIdHexLength} hex chars)", []);

        Span<byte> stack = stackalloc byte[PayloadIdByteLength];
        if (Convert.FromHexString(hex, stack, out _, out _) != OperationStatus.Done)
            return Result<byte[]>.Fail($"Invalid payload ID: '{extra}'", []);

        return Result<byte[]>.Success(stack.ToArray());
    }
}

public partial struct ExchangeCapabilitiesRequestWire : ISszRpcRequest<ExchangeCapabilitiesRequestWire, string[]>
{
    public static Result<string[]> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ExchangeCapabilitiesRequestWire wire);
        if (wire.Capabilities is null) return Result<string[]>.Success([]);

        string[] result = new string[wire.Capabilities.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Encoding.UTF8.GetString(wire.Capabilities[i].Name ?? []);

        return Result<string[]>.Success(result);
    }
}

public partial struct GetClientVersionRequestWire : ISszRpcRequest<GetClientVersionRequestWire, ClientVersionV1>
{
    public static Result<ClientVersionV1> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
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

        return Result<ClientVersionV1>.Success(clientVersion);
    }
}

public partial struct ForkchoiceUpdatedV1RequestWire : ISszRpcRequest<ForkchoiceUpdatedV1RequestWire, ForkchoiceUpdatedArguments>
{
    public static Result<ForkchoiceUpdatedArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV1RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return Result<ForkchoiceUpdatedArguments>.Success(
            new(SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs, Timestamp: attrs?.Timestamp));
    }
}

public partial struct ForkchoiceUpdatedV2RequestWire : ISszRpcRequest<ForkchoiceUpdatedV2RequestWire, ForkchoiceUpdatedArguments>
{
    public static Result<ForkchoiceUpdatedArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV2RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return Result<ForkchoiceUpdatedArguments>.Success(
            new(SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs, Timestamp: attrs?.Timestamp));
    }
}

public partial struct ForkchoiceUpdatedV3RequestWire : ISszRpcRequest<ForkchoiceUpdatedV3RequestWire, ForkchoiceUpdatedArguments>
{
    public static Result<ForkchoiceUpdatedArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedV3RequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        return Result<ForkchoiceUpdatedArguments>.Success(
            new(SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs, Timestamp: attrs?.Timestamp));
    }
}

public partial struct ForkchoiceUpdatedRequestWire : ISszRpcRequest<ForkchoiceUpdatedRequestWire, ForkchoiceUpdatedArguments>
{
    public static Result<ForkchoiceUpdatedArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out ForkchoiceUpdatedRequestWire wire);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszRpcMessageAdapter.PayloadAttributesFromWire(a[0]) : null;
        BitArray? custodyColumns = wire.CustodyColumns is { Length: > 0 } c ? c[0].Bits : null;
        return Result<ForkchoiceUpdatedArguments>.Success(
            new(SszRpcMessageAdapter.ForkchoiceStateV1FromWire(wire.ForkchoiceState), attrs, custodyColumns, attrs?.Timestamp));
    }
}

public partial struct NewPayloadV1RequestWire : ISszRpcRequest<NewPayloadV1RequestWire, ExecutionPayload>
{
    public static Result<ExecutionPayload> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV1RequestWire wire);
        return Result<ExecutionPayload>.Success(wire.ExecutionPayload.AsExecutionPayload());
    }
}

public partial struct NewPayloadV2RequestWire : ISszRpcRequest<NewPayloadV2RequestWire, ExecutionPayload>
{
    public static Result<ExecutionPayload> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV2RequestWire wire);
        return Result<ExecutionPayload>.Success(wire.ExecutionPayload.AsExecutionPayload());
    }
}

public partial struct NewPayloadV3RequestWire : ISszRpcRequest<NewPayloadV3RequestWire, NewPayloadV3Arguments>
{
    public static Result<NewPayloadV3Arguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV3RequestWire wire);
        ExecutionPayloadV3 executionPayload = wire.ExecutionPayload.AsExecutionPayload();
        return Result<NewPayloadV3Arguments>.Success(new(
            executionPayload,
            SszRpcMessageAdapter.GetBlobVersionedHashes(executionPayload),
            wire.ParentBeaconBlockRoot));
    }
}

public partial struct NewPayloadV4RequestWire : ISszRpcRequest<NewPayloadV4RequestWire, NewPayloadV4Arguments>
{
    public static Result<NewPayloadV4Arguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV4RequestWire wire);
        ExecutionPayloadV3 executionPayload = wire.ExecutionPayload.AsExecutionPayload();
        return Result<NewPayloadV4Arguments>.Success(new(
            executionPayload,
            SszRpcMessageAdapter.GetBlobVersionedHashes(executionPayload),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ));
    }
}

public partial struct NewPayloadV5RequestWire : ISszRpcRequest<NewPayloadV5RequestWire, NewPayloadV5Arguments>
{
    public static Result<NewPayloadV5Arguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out NewPayloadV5RequestWire wire);
        ExecutionPayloadV4 executionPayload = wire.ExecutionPayload.AsExecutionPayload();
        return Result<NewPayloadV5Arguments>.Success(new(
            executionPayload,
            SszRpcMessageAdapter.GetBlobVersionedHashes(executionPayload),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ));
    }
}

public partial struct GetBlobsRequestWire : ISszRpcRequest<GetBlobsRequestWire, byte[][]>
{
    public static Result<byte[][]> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetBlobsRequestWire wire);
        if (wire.VersionedHashes is null) return Result<byte[][]>.Success([]);

        byte[][] result = new byte[wire.VersionedHashes.Length][];
        for (int i = 0; i < result.Length; i++)
            result[i] = wire.VersionedHashes[i].Bytes.ToArray();

        return Result<byte[][]>.Success(result);
    }
}

public partial struct GetPayloadBodiesByHashRequestWire : ISszRpcRequest<GetPayloadBodiesByHashRequestWire, Hash256[]>
{
    public static Result<Hash256[]> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetPayloadBodiesByHashRequestWire wire);
        return Result<Hash256[]>.Success(wire.BlockHashes ?? []);
    }
}

public partial struct GetPayloadBodiesByRangeRequestWire : ISszRpcRequest<GetPayloadBodiesByRangeRequestWire, PayloadBodiesByRangeArguments>
{
    public static Result<PayloadBodiesByRangeArguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        if (!TryReadQuery(ctx, "from", out ulong start))
            return Result<PayloadBodiesByRangeArguments>.Fail("Missing or invalid 'from' query parameter", default);

        if (!TryReadQuery(ctx, "count", out ulong count))
            return Result<PayloadBodiesByRangeArguments>.Fail("Missing or invalid 'count' query parameter", default);

        return Result<PayloadBodiesByRangeArguments>.Success(
            new(SszNumericChecks.CheckedLong(start), SszNumericChecks.CheckedLong(count)));
    }

    private static bool TryReadQuery(HttpContext ctx, string key, out ulong value)
    {
        value = 0;
        return ctx.Request.Query.TryGetValue(key, out StringValues values)
            && values.Count == 1
            && ulong.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

public partial struct GetBlobsV4RequestWire : ISszRpcRequest<GetBlobsV4RequestWire, GetBlobsV4Arguments>
{
    public static Result<GetBlobsV4Arguments> DecodeArguments(HttpContext ctx, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Decode(body, out GetBlobsV4RequestWire wire);
        if (wire.BlobVersionedHashes is null)
            return Result<GetBlobsV4Arguments>.Success(new([], wire.IndicesBitarray ?? new BitArray(128)));

        byte[][] hashes = new byte[wire.BlobVersionedHashes.Length][];
        for (int i = 0; i < hashes.Length; i++)
            hashes[i] = wire.BlobVersionedHashes[i].Bytes.ToArray();

        return Result<GetBlobsV4Arguments>.Success(new(hashes, wire.IndicesBitarray ?? new BitArray(128)));
    }
}

public partial struct PayloadStatusWire : INew<PayloadStatusV1, PayloadStatusWire>
{
    public static PayloadStatusWire New(PayloadStatusV1 arg) => SszRpcMessageAdapter.BuildPayloadStatusWire(arg);
}

public partial struct ForkchoiceUpdatedResponseWire : INew<ForkchoiceUpdatedV1Result, ForkchoiceUpdatedResponseWire>
{
    public static ForkchoiceUpdatedResponseWire New(ForkchoiceUpdatedV1Result arg)
    {
        SszPayloadId[]? pidList = null;
        if (arg.PayloadId is not null)
        {
            ReadOnlySpan<char> hex = arg.PayloadId.AsSpan();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (hex.Length != 16)
                throw new InvalidOperationException($"Invalid payload id '{arg.PayloadId}': expected 16 hex chars, got {hex.Length}");

            Span<byte> stack = stackalloc byte[8];
            Bytes.FromHexString(hex, stack);
            pidList = [new SszPayloadId { Bytes = stack.ToArray() }];
        }

        return new ForkchoiceUpdatedResponseWire
        {
            PayloadStatus = SszRpcMessageAdapter.BuildPayloadStatusWire(arg.PayloadStatus),
            PayloadId = pidList ?? []
        };
    }
}

public partial class SszExecutionPayloadV1 : INew<ExecutionPayload, SszExecutionPayloadV1>
{
    public static SszExecutionPayloadV1 New(ExecutionPayload arg) => new(arg);
}

public partial struct GetPayloadResponseV2Wire : INew<GetPayloadV2Result, GetPayloadResponseV2Wire>
{
    public static GetPayloadResponseV2Wire New(GetPayloadV2Result arg) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV2(arg.ExecutionPayload),
        BlockValue = arg.BlockValue
    };
}

public partial struct GetPayloadResponseV3Wire : INew<GetPayloadV3Result, GetPayloadResponseV3Wire>
{
    public static GetPayloadResponseV3Wire New(GetPayloadV3Result arg) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3(arg.ExecutionPayload),
        BlockValue = arg.BlockValue,
        BlobsBundle = arg.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = arg.ShouldOverrideBuilder
    };
}

public partial struct GetPayloadResponseV4Wire : INew<GetPayloadV4Result, GetPayloadResponseV4Wire>
{
    public static GetPayloadResponseV4Wire New(GetPayloadV4Result arg) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3(arg.ExecutionPayload),
        BlockValue = arg.BlockValue,
        BlobsBundle = arg.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = arg.ShouldOverrideBuilder,
        ExecutionRequests = arg.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetPayloadResponseV5Wire : INew<GetPayloadV5Result, GetPayloadResponseV5Wire>
{
    public static GetPayloadResponseV5Wire New(GetPayloadV5Result arg) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV3(arg.ExecutionPayload),
        BlockValue = arg.BlockValue,
        BlobsBundle = arg.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = arg.ShouldOverrideBuilder,
        ExecutionRequests = arg.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetPayloadResponseV6Wire : INew<GetPayloadV6Result, GetPayloadResponseV6Wire>
{
    public static GetPayloadResponseV6Wire New(GetPayloadV6Result arg) => new()
    {
        ExecutionPayload = new SszExecutionPayloadV4(arg.ExecutionPayload),
        BlockValue = arg.BlockValue,
        BlobsBundle = arg.BlobsBundle.ToWire(),
        ShouldOverrideBuilder = arg.ShouldOverrideBuilder,
        ExecutionRequests = arg.ExecutionRequests.ToExecutionRequestsWire()
    };
}

public partial struct GetBlobsV1ResponseWire : INew<IReadOnlyList<BlobAndProofV1?>, GetBlobsV1ResponseWire>
{
    public static GetBlobsV1ResponseWire New(IReadOnlyList<BlobAndProofV1?> arg)
    {
        int count = arg.Count;
        BlobV1EntryWire[] arr = new BlobV1EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV1? blobAndProof = arg[i];
            arr[i] = blobAndProof is null
                ? new BlobV1EntryWire { Available = false, Contents = default }
                : new BlobV1EntryWire { Available = true, Contents = new BlobAndProofV1Wire { Blob = blobAndProof.Blob, Proof = blobAndProof.Proof } };
        }

        return new GetBlobsV1ResponseWire { Entries = arr };
    }
}

public partial struct GetBlobsV2ResponseWire : INew<IReadOnlyList<BlobAndProofV2?>, GetBlobsV2ResponseWire>
{
    public static GetBlobsV2ResponseWire New(IReadOnlyList<BlobAndProofV2?> arg)
    {
        int count = arg.Count;
        BlobV2EntryWire[] arr = new BlobV2EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? blobAndProof = arg[i];
            arr[i] = blobAndProof is null
                ? new BlobV2EntryWire { Available = false, Contents = default }
                : new BlobV2EntryWire { Available = true, Contents = new BlobAndProofV2Wire { Blob = blobAndProof.Blob, Proofs = blobAndProof.Proofs.ToKzgWire() } };
        }

        return new GetBlobsV2ResponseWire { Entries = arr };
    }
}

public partial struct GetBlobsV3ResponseWire : INew<IReadOnlyList<BlobAndProofV2?>, GetBlobsV3ResponseWire>
{
    public static GetBlobsV3ResponseWire New(IReadOnlyList<BlobAndProofV2?> arg)
    {
        int count = arg.Count;
        BlobV3EntryWire[] arr = new BlobV3EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? blobAndProof = arg[i];
            arr[i] = blobAndProof is null
                ? new BlobV3EntryWire { Available = false, Contents = default }
                : new BlobV3EntryWire { Available = true, Contents = new BlobAndProofV2Wire { Blob = blobAndProof.Blob, Proofs = blobAndProof.Proofs.ToKzgWire() } };
        }

        return new GetBlobsV3ResponseWire { Entries = arr };
    }
}

public partial struct GetBlobsV4ResponseWire : INew<IReadOnlyList<BlobCellsAndProofs?>, GetBlobsV4ResponseWire>
{
    public static GetBlobsV4ResponseWire New(IReadOnlyList<BlobCellsAndProofs?> arg)
    {
        int count = arg.Count;
        BlobV4EntryWire[] arr = new BlobV4EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobCellsAndProofs? blobCellsAndProofs = arg[i];
            if (blobCellsAndProofs is null || !blobCellsAndProofs.Available)
            {
                arr[i] = new BlobV4EntryWire { Available = false, Contents = default };
                continue;
            }

            NullableBlobCellWire[] cells = new NullableBlobCellWire[128];
            NullableKzgProofWire[] proofs = new NullableKzgProofWire[128];
            for (int j = 0; j < 128; j++)
            {
                byte[]? cell = blobCellsAndProofs.BlobCells?[j];
                byte[]? proof = blobCellsAndProofs.Proofs?[j];
                cells[j] = cell is null
                    ? new NullableBlobCellWire { Cell = [] }
                    : new NullableBlobCellWire { Cell = [SszBlobCell.FromSpan(cell)] };
                proofs[j] = proof is null
                    ? new NullableKzgProofWire { Proof = [] }
                    : new NullableKzgProofWire { Proof = [SszKzgCommitment.FromSpan(proof)] };
            }

            arr[i] = new BlobV4EntryWire
            {
                Available = true,
                Contents = new BlobCellsAndProofsWire { BlobCells = cells, Proofs = proofs }
            };
        }

        return new GetBlobsV4ResponseWire { Entries = arr };
    }
}

public partial struct PayloadBodiesV1ResponseWire : INew<IReadOnlyList<ExecutionPayloadBodyV1Result?>, PayloadBodiesV1ResponseWire>
{
    public static PayloadBodiesV1ResponseWire New(IReadOnlyList<ExecutionPayloadBodyV1Result?> arg)
    {
        int count = arg.Count;
        BodyEntryV1Wire[] arr = new BodyEntryV1Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV1Result? body = arg[i];
            arr[i] = body is null
                ? new BodyEntryV1Wire { Available = false, Body = default }
                : new BodyEntryV1Wire { Available = true, Body = body.ToBodyWire() };
        }

        return new PayloadBodiesV1ResponseWire { Entries = arr };
    }
}

public partial struct PayloadBodiesV2ResponseWire : INew<IReadOnlyList<ExecutionPayloadBodyV2Result?>, PayloadBodiesV2ResponseWire>
{
    public static PayloadBodiesV2ResponseWire New(IReadOnlyList<ExecutionPayloadBodyV2Result?> arg)
    {
        int count = arg.Count;
        BodyEntryV2Wire[] arr = new BodyEntryV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV2Result? body = arg[i];
            arr[i] = body is null
                ? new BodyEntryV2Wire { Available = false, Body = default }
                : new BodyEntryV2Wire { Available = true, Body = body.ToBodyWire() };
        }

        return new PayloadBodiesV2ResponseWire { Entries = arr };
    }
}

public partial struct ExchangeCapabilitiesResponseWire : INew<IReadOnlyList<string>, ExchangeCapabilitiesResponseWire>
{
    public static ExchangeCapabilitiesResponseWire New(IReadOnlyList<string> arg)
    {
        int count = arg.Count;
        SszCapabilityName[] arr = new SszCapabilityName[count];
        for (int i = 0; i < count; i++)
            arr[i] = new SszCapabilityName { Name = Encoding.UTF8.GetBytes(arg[i]) };

        return new ExchangeCapabilitiesResponseWire { Capabilities = arr };
    }
}

public partial struct GetClientVersionResponseWire : INew<ClientVersionV1[], GetClientVersionResponseWire>
{
    public static GetClientVersionResponseWire New(ClientVersionV1[] arg)
    {
        ClientVersionWire[] wireVersions = new ClientVersionWire[arg.Length];
        for (int i = 0; i < arg.Length; i++)
        {
            string commitHex = arg[i].Commit ?? string.Empty;
            byte[] commit = commitHex.Length >= 8
                ? Convert.FromHexString(commitHex[..8])
                : new byte[4];
            wireVersions[i] = new ClientVersionWire
            {
                Code = Encoding.UTF8.GetBytes(arg[i].Code ?? string.Empty),
                Name = Encoding.UTF8.GetBytes(arg[i].Name ?? string.Empty),
                Version = Encoding.UTF8.GetBytes(arg[i].Version ?? string.Empty),
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

    public static Hash256?[] GetBlobVersionedHashes(ExecutionPayload payload)
    {
        Hash256[] hashes = SszCodec.GetBlobVersionedHashes(payload);
        if (hashes.Length == 0) return [];

        Hash256?[] result = new Hash256?[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
            result[i] = hashes[i];

        return result;
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
