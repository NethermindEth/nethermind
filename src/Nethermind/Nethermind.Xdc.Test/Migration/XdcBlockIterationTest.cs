// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using Xdc;
using static Nethermind.Core.BlockHeader.Format;

namespace Nethermind.Xdc.Test.Migration;

public class XdcBlockIterationTest
{
    private static readonly XdcHeaderDecoder HeaderDecoder = new();

    // from schema.go
    private static readonly byte[] HeadHeaderKey = "LastHeader"u8.ToArray();
    private static readonly byte[] HeaderPrefix = "h"u8.ToArray();
    private static readonly byte[] HeaderNumberPrefix = "H"u8.ToArray();

    // from engine_v2/snapshot.go
    private static readonly byte[] SnapshotV2Prefix = "XDPoS-V2-"u8.ToArray();

    private static void IterateBackward(string dbPath, Func<IDb, XdcBlockHeader, bool> callback)
    {
        using var db = new ReadOnlyLevelDb(dbPath);

        byte[]? currentHash = db.Get(HeadHeaderKey);
        Assert.That(currentHash, Is.Not.Null.And.Not.Empty, "LastHeader not found");

        XdcBlockHeader? header = GetHeaderByHash(db, currentHash);
        Assert.That(header, Is.Not.Null, "Failed to decode latest header");

        int filteredCount = 0;

        TestContext.Out.WriteLine($"Starting at block {header.ToString(FullHashAndNumber)}\n");

        XdcBlockHeader? lastHeader = null;
        while (header is not null)
        {
            if (callback(db, header))
                filteredCount++;

            if (header.ParentHash is null || header.ParentHash == Keccak.Zero)
                break;

            header = GetHeaderByHash(db, header.ParentHash.Bytes.ToArray());

            if (header is not null)
                lastHeader = header;
        }

        TestContext.Out.WriteLine($"\nIteration complete, last block: {lastHeader?.ToString(FullHashAndNumber)}");
        Assert.That(filteredCount, Is.GreaterThan(0));
    }

    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void IterateBackwardEpochSwitch(string dbPath)
    {
        IterateBackward(dbPath, (_, header) =>
        {
            if (header.Validators is null || header.Validators.Length <= 0)
                return false;

            TestContext.Out.WriteLine($"Epoch switch block: {header.ToString(FullHashAndNumber)}, Validators: [{header.ValidatorsAddress!.Value.Length}]");
            return true;
        });
    }

    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void IterateBackwardSnapshots(string dbPath)
    {
        IterateBackward(dbPath, (db, header) =>
        {
            if (TryGetSnapshot(db, header) is not {} snap)
                return false;

            TestContext.Out.WriteLine($"Snapshot @{snap.BlockNumber}, Header: {snap.HeaderHash}, Candidates: [{snap.NextEpochCandidates.Length}]");
            return true;
        });
    }

    private static XdcBlockHeader? GetHeaderByHash(IDb levelDb, byte[] hash)
    {
        // "H" + hash
        byte[] headerNumberKey = [.. HeaderNumberPrefix, .. hash];
        byte[]? blockNumberBytes = levelDb.Get(headerNumberKey);
        if (blockNumberBytes is null || blockNumberBytes.Length != 8)
            return null;

        ulong blockNumber = BinaryPrimitives.ReadUInt64BigEndian(blockNumberBytes);

        // "h" + number(8 bytes BE) + hash
        byte[] headerKey = [.. HeaderPrefix, .. blockNumber.ToBigEndianByteArray(), .. hash];
        byte[]? headerRlp = levelDb.Get(headerKey);
        if (headerRlp is null)
            return null;

        return HeaderDecoder.Decode(new(headerRlp)) as XdcBlockHeader;
    }

    // from engine_v2/snapshot.go
    private static Snapshot? TryGetSnapshot(IDb levelDb, XdcBlockHeader header)
    {
        byte[] key = [.. SnapshotV2Prefix, .. header.Hash!.Bytes];
        if (levelDb.Get(key) is not {} jsonBytes)
            return null;

        if (JsonSerializer.Deserialize<XdcSnapshotJson>(jsonBytes) is not {} xdcSnap)
            return null;

        return new(
            (long)xdcSnap.Number,
            new(xdcSnap.Hash),
            xdcSnap.MasterNodes?.Select(h => new Address(h)).ToArray() ?? []
        );
    }

    private class XdcSnapshotJson
    {
        [JsonPropertyName("number")]
        public ulong Number { get; set; }

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("masterNodes")]
        public string[]? MasterNodes { get; set; }
    }
}
