// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public void GetPayloadV5Result_serializes_v2_blob_bundle()
    {
        byte[] commitment = new byte[48];
        commitment[0] = 1;
        byte[] blob = new byte[32];
        blob[0] = 2;
        byte[] proof = new byte[48];
        proof[0] = 3;
        Block block = Build.A.Block.TestObject;
        GetPayloadV5Result result = new(block, UInt256.One, new BlobsBundleV2([commitment], [blob], [proof]), [], shouldOverrideBuilder: false);

        string json = JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions);
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode blobsBundle = root["blobsBundle"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobsBundle, Is.Not.Null);
            Assert.That((string?)blobsBundle["commitments"]![0], Does.StartWith("0x01"));
            Assert.That((string?)blobsBundle["blobs"]![0], Does.StartWith("0x02"));
            Assert.That((string?)blobsBundle["proofs"]![0], Does.StartWith("0x03"));
        }
    }
}
