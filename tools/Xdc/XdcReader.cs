// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;

namespace Xdc;

public class XdcReader(IDb db)
{
    private const int EpochLength = 900;
    private const int Gap = 450;

    // from schema.go
    private static readonly byte[] HeadHeaderKey = "LastHeader"u8.ToArray();
    private static readonly byte[] HeaderPrefix = "h"u8.ToArray();
    private static readonly byte[] HeaderNumberPrefix = "H"u8.ToArray();
    private static readonly byte[] HeaderHashSuffix = "n"u8.ToArray();
    private static readonly byte[] HeaderTDPostfix = "t"u8.ToArray();
    private static readonly byte[] CodePrefix = "c"u8.ToArray();

    // from engine_v2/snapshot.go
    private static readonly byte[] SnapshotV2Prefix = "XDPoS-V2-"u8.ToArray();
    private record XdcSnapshotJson(
        [property: JsonPropertyName("number")] ulong Number,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("masterNodes")] string[]? MasterNodes
    );

    private static readonly XdcHeaderDecoder HeaderDecoder = new();

    public XdcBlockHeader? GetHeadHeader(bool includeTD = true) => db.Get(HeadHeaderKey) is not { } hash ? null : GetHeader(hash, includeTD);

    public Snapshot? GetSnapshotFor(long blockNumber)
    {
        var snapNumber = Math.Max(0, blockNumber - blockNumber % EpochLength - Gap);
        return GetHeader(snapNumber, false) is not { } snapHeader ? null : GetSnapshotAt(snapHeader);
    }

    public Snapshot? GetLatestSnapshot(long atOrBefore)
    {
        var snapNumber = Math.Max(0, atOrBefore - atOrBefore % EpochLength + EpochLength - Gap);
        if (snapNumber >= atOrBefore) snapNumber -= EpochLength;
        return GetHeader(snapNumber, false) is not { } snapHeader ? null : GetSnapshotAt(snapHeader);
    }

    public Snapshot? GetSnapshotAt(XdcBlockHeader header)
    {
        byte[] key = [.. SnapshotV2Prefix, .. header.Hash!.Bytes];
        if (db.Get(key) is not {} jsonBytes)
            return null;

        if (JsonSerializer.Deserialize<XdcSnapshotJson>(jsonBytes) is not {} xdcSnap)
            return null;

        return new(
            (long)xdcSnap.Number,
            new(xdcSnap.Hash),
            xdcSnap.MasterNodes?.Select(h => new Address(h)).ToArray() ?? []
        );
    }

    public XdcBlockHeader? GetHeader(Hash256 hash, bool includeTD = true) => GetHeader(hash.Bytes, includeTD);

    public XdcBlockHeader? GetHeader(ReadOnlySpan<byte> hash, bool includeTD = true)
    {
        // "H" + hash -> number
        byte[] headerNumberKey = [.. HeaderNumberPrefix, .. hash];
        byte[]? blockNumberBytes = db.Get(headerNumberKey);
        if (blockNumberBytes is null || blockNumberBytes.Length != 8)
            return null;

        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(blockNumberBytes);
        return GetHeader(hash, blockNumber, includeTD);
    }

    public XdcBlockHeader? GetHeader(long blockNumber, bool includeTD = true)
    {
        // "h" + number(8 bytes BE) + "n" -> canonical hash
        byte[] hashKey = [.. HeaderPrefix, .. blockNumber.ToBigEndianByteArray(), .. HeaderHashSuffix];
        return db.Get(hashKey) is not { Length: 32 } hash ? null : GetHeader(hash, blockNumber, includeTD);
    }

    private XdcBlockHeader? GetHeader(ReadOnlySpan<byte> hash, long blockNumber, bool includeTD)
    {
        // "h" + number(8 bytes BE) + hash -> header RLP
        byte[] headerKey = [.. HeaderPrefix, .. blockNumber.ToBigEndianByteArray(), .. hash];
        byte[]? headerRlp = db.Get(headerKey);
        if (headerRlp is null)
            return null;

        var header = HeaderDecoder.Decode(new(headerRlp)) as XdcBlockHeader;

        if (includeTD && !TrySetTD(header))
            return null;

        return header;
    }

    private bool TrySetTD(XdcBlockHeader? header)
    {
        if (header?.Hash is null)
            return false;

        // "h" + number(8 bytes BE) + hash + "t"
        byte[] tdKey = [.. HeaderPrefix, .. header.Number.ToBigEndianByteArray(), .. header.Hash.Bytes, .. HeaderTDPostfix];
        byte[]? tdRlp = db.Get(tdKey);
        if (tdRlp is null)
            return false;

        Rlp.ValueDecoderContext ctx = new(tdRlp);
        header.TotalDifficulty = ctx.DecodeUInt256();
        return true;
    }

    public byte[]? GetCode(Hash256 hash)
    {
        if (hash is not {Bytes: var keyBytes})
            return null;

        keyBytes = (byte[]) [..CodePrefix, ..keyBytes];
        return db.Get(keyBytes);
    }
}
