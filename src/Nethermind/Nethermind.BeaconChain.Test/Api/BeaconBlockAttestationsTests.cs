// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconBlockAttestationsTests : BeaconApiFixture
{
    private const string Json = "application/json";

    private const ulong RichSlot = 412_500 * 32 + 7;
    private const ulong EmptySlot = RichSlot + 1;
    private static readonly Hash256 RichRoot = TestRoot(0xa0);
    private static readonly Hash256 EmptyRoot = TestRoot(0xa1);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await StartAsync(BeaconChainSpec.Mainnet);
        _host.Store.PutBlock(RichRoot, RichBlock(RichSlot, FilledHash(0x00)));
        _host.Store.SetCanonicalRoot(RichSlot, RichRoot);
        _host.Store.PutBlock(EmptyRoot, MinimalBlock(EmptySlot));
        _host.Store.SetCanonicalRoot(EmptySlot, EmptyRoot);
    }

    [SetUp]
    public void ResetSharedState() => _host.SetStatus(RichRoot, Hash256.Zero, 0);

    [Test]
    public async Task Attestations_are_the_body_attestations_in_beacon_api_encoding()
    {
        JsonElement root = await ReadFuluEnvelope($"/eth/v2/beacon/blocks/{RichRoot}/attestations", expectedFinalized: false);
        JsonElement data = root.GetProperty("data");
        Assert.That(data.GetArrayLength(), Is.EqualTo(1));
        JsonElement attestation = data[0];

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attestation.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "aggregation_bits", "data", "signature", "committee_bits" }));
        // Bits {0, 2} of a 3-bit bitlist plus the length sentinel at bit 3.
        Assert.That(attestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x0d"));
        Assert.That(attestation.GetProperty("committee_bits").GetString(), Is.EqualTo("0x0200000000000000"));
        Assert.That(attestation.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x43)));
        JsonElement attestationData = attestation.GetProperty("data");
        Assert.That(attestationData.GetProperty("slot").GetString(), Is.EqualTo((RichSlot - 1).ToString()));
        Assert.That(attestationData.GetProperty("index").GetString(), Is.EqualTo("0"));
        Assert.That(attestationData.GetProperty("beacon_block_root").GetString(), Is.EqualTo(Hex(32, 0xaa)));
        Assert.That(attestationData.GetProperty("source").GetProperty("epoch").GetString(), Is.EqualTo("412498"));
        Assert.That(attestationData.GetProperty("source").GetProperty("root").GetString(), Is.EqualTo(Hex(32, 0xab)));
        Assert.That(attestationData.GetProperty("target").GetProperty("epoch").GetString(), Is.EqualTo("412499"));
        Assert.That(attestationData.GetProperty("target").GetProperty("root").GetString(), Is.EqualTo(Hex(32, 0xac)));
    }

    [Test]
    public async Task Block_without_attestations_gives_an_empty_list()
    {
        JsonElement root = await ReadFuluEnvelope($"/eth/v2/beacon/blocks/{EmptySlot}/attestations", expectedFinalized: false);
        Assert.That(root.GetProperty("data").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Finalized_flag_follows_the_finalized_checkpoint()
    {
        _host.SetStatus(RichRoot, RichRoot, 412_501);
        await ReadFuluEnvelope($"/eth/v2/beacon/blocks/{RichSlot}/attestations", expectedFinalized: true);
    }

    [TestCase("not-a-block", Json, HttpStatusCode.BadRequest)]
    [TestCase("1", Json, HttpStatusCode.NotFound)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000ee", Json, HttpStatusCode.NotFound)]
    [TestCase("head", "application/octet-stream", HttpStatusCode.NotAcceptable)]
    public async Task Errors_follow_the_published_responses(string blockId, string accept, HttpStatusCode expected)
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{blockId}/attestations", accept);
        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }
}
