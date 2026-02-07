// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;

namespace Xdc;

public class XdcReader(IDb db)
{
    // from schema.go
    private static readonly byte[] HeadHeaderKey = "LastHeader"u8.ToArray();
    private static readonly byte[] HeaderPrefix = "h"u8.ToArray();
    private static readonly byte[] HeaderNumberPrefix = "H"u8.ToArray();
    private static readonly byte[] CodePrefix = "c"u8.ToArray();

    // from engine_v2/snapshot.go
    private static readonly byte[] SnapshotV2Prefix = "XDPoS-V2-"u8.ToArray();
    private record XdcSnapshotJson(
        [property: JsonPropertyName("number")] ulong Number,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("masterNodes")] string[]? MasterNodes
    );

    private static readonly XdcHeaderDecoder HeaderDecoder = new();

    public XdcBlockHeader? GetHeadHeader() => db.Get(HeadHeaderKey) is not { } hash ? null : GetHeader(hash);

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

    public XdcBlockHeader? GetHeader(Hash256 hash) => GetHeader(hash.Bytes);

    public XdcBlockHeader? GetHeader(ReadOnlySpan<byte> hash)
    {
        // "H" + hash
        byte[] headerNumberKey = [.. HeaderNumberPrefix, .. hash];
        byte[]? blockNumberBytes = db.Get(headerNumberKey);
        if (blockNumberBytes is null || blockNumberBytes.Length != 8)
            return null;

        ulong blockNumber = BinaryPrimitives.ReadUInt64BigEndian(blockNumberBytes);

        // "h" + number(8 bytes BE) + hash
        byte[] headerKey = [.. HeaderPrefix, .. blockNumber.ToBigEndianByteArray(), .. hash];
        byte[]? headerRlp = db.Get(headerKey);
        if (headerRlp is null)
            return null;

        return HeaderDecoder.Decode(new(headerRlp)) as XdcBlockHeader;
    }

    public byte[]? GetCode(Hash256 hash)
    {
        if (hash is not {Bytes: var keyBytes})
            return null;

        keyBytes = (byte[]) [..CodePrefix, ..keyBytes];
        return db.Get(keyBytes);
    }
}
