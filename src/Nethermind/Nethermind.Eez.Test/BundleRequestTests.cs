// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Eez.Posting;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class BundleRequestTests
{
    private static readonly byte[][] Transactions = [[0x02, 0xab]];

    [Test]
    public void Serialize_Pinned_WritesTheTimestampBoundsAsNumbers()
    {
        JsonElement bundle = Serialize(new BundleTarget(11168, 1_790_683_736));

        Assert.That((bundle.GetProperty("minTimestamp").ValueKind, bundle.GetProperty("maxTimestamp").ValueKind), Is.EqualTo((JsonValueKind.Number, JsonValueKind.Number)),
            "the builder accepts hex bounds and then never includes the bundle");
        Assert.That((bundle.GetProperty("minTimestamp").GetUInt64(), bundle.GetProperty("maxTimestamp").GetUInt64()), Is.EqualTo((1_790_683_736UL, 1_790_683_736UL)),
            "pinned to the one L1 timestamp the Sync block carries");
        Assert.That((bundle.GetProperty("blockNumber").GetString(), bundle.GetProperty("txs")[0].GetString()), Is.EqualTo(("0x2ba0", "0x02ab")),
            "the block and transactions stay hex, as the builder reads them");
    }

    [Test]
    public void Serialize_NextBlock_HasNoTimestampBounds()
    {
        JsonElement bundle = Serialize(BundleTarget.NextBlock);

        Assert.That(bundle.TryGetProperty("minTimestamp", out _) || bundle.TryGetProperty("maxTimestamp", out _), Is.False,
            "a catch-up batch lands in whichever block takes it");
    }

    private static JsonElement Serialize(BundleTarget target) =>
        JsonDocument.Parse(new EthereumJsonSerializer().Serialize(BundleRequest.Create(Transactions, 11168, target))).RootElement;
}
