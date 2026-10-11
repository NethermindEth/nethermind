// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconApiErrorMappingTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;

    private const string UnrelatedInternalDetail = "internal detail from an unrelated layer";

    private BeaconApiTestHost _host = null!;
    private WebApplication _pipeline = null!;
    private HttpClient _pipelineClient = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(Spec, forkAwareStore: false);
        _host.Client.Timeout = TimeSpan.FromSeconds(5);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _pipeline = builder.Build();
        IBeaconChainStatusSource failingStatus = Substitute.For<IBeaconChainStatusSource>();
        failingStatus.CurrentHead.Throws(new InvalidOperationException(UnrelatedInternalDetail));
        BeaconApiEndpoints.MapAll(_pipeline, new BeaconApiContext(new BeaconChainConfig(), Spec, failingStatus, _host.Clock, _host.Store,
            new LocalMetadataSource(), new NoOpEngineDriver(), LimboLogs.Instance, null, null, null));
        _pipeline.MapGet("/test/not-supported", (HttpContext _) => { throw new NotSupportedException(UnrelatedInternalDetail); });
        _pipeline.MapGet("/test/unsupported-fork", (HttpContext _) => { throw new UnsupportedForkException(new NotSupportedException(UnrelatedInternalDetail)); });
        _pipeline.MapGet("/test/fault-after-flush", FaultAfterFirstFlush);
        await _pipeline.StartAsync();
        _pipelineClient = new HttpClient { BaseAddress = new Uri(_pipeline.Urls.First()), Timeout = TimeSpan.FromSeconds(5) };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _host.Client.Dispose();
        _pipelineClient.Dispose();
        await _host.Host.DisposeAsync();
        await _pipeline.DisposeAsync();
        _host.Db.Dispose();
    }

    [TestCase("/test/not-supported", 500, "Internal server error", TestName = "An_unrelated_NotSupportedException_is_a_500_that_leaks_nothing")]
    [TestCase("/test/unsupported-fork", 501, BeaconApiEndpoints.UnsupportedForkMessage, TestName = "The_API_owned_unsupported_fork_exception_is_a_501_with_the_fixed_message")]
    [TestCase("/eth/v1/node/syncing", 500, "Internal server error", "application/octet-stream", TestName = "Snapshot_failure_precedes_JSON_negotiation")]
    public async Task Exceptions_return_their_public_status_and_message_without_internal_details(string route, int status, string message, string? accept = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, route);
        if (accept is not null) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using HttpResponseMessage response = await _pipelineClient.SendAsync(request);
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(status), $"body: {raw}");
        Assert.That(raw, Does.Not.Contain(UnrelatedInternalDetail), "an exception's own text must never reach the caller");
        using JsonDocument body = JsonDocument.Parse(raw);
        Assert.That(body.RootElement.GetProperty("message").GetString(), Is.EqualTo(message));
    }

    private static async Task FaultAfterFirstFlush(HttpContext c)
    {
        const long fillerLimit = 1024 * 1024;
        c.Response.ContentType = ContentNegotiation.Json;
        await using BeaconJsonStream stream = new(c.Response.BodyWriter, c.RequestAborted);
        stream.Writer.WriteStartArray();
        while (!c.Response.HasStarted && stream.Writer.BytesCommitted < fillerLimit)
        {
            stream.Writer.WriteStringValue(UnrelatedInternalDetail);
            await stream.CheckpointAsync();
        }

        // Whatever the checkpoint did, the failure below must land after bytes are on the wire.
        await stream.FlushAsync();
        throw new InvalidOperationException(UnrelatedInternalDetail);
    }

    [Test]
    public void A_handler_failure_after_the_first_flush_is_a_transport_error_not_a_well_terminated_200() =>
        Assert.ThrowsAsync<HttpRequestException>(() => _pipelineClient.GetAsync("/test/fault-after-flush"),
            "the client must observe a failed transfer, not a 200 with a truncated body");

    /// <summary>Minimal bytes for BeaconStateCodec to read a slot: it never reaches the full decode
    /// once the slot resolves to a fork this driver refuses, so nothing past offset 48 matters.</summary>
    private static byte[] StateBytesForSlot(ulong slot)
    {
        byte[] ssz = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(40), slot);
        return ssz;
    }

    [TestCase("/eth/v1/beacon/states/{0}/finality_checkpoints")]
    [TestCase("/eth/v1/beacon/states/{0}/validators")]
    [TestCase("/eth/v1/beacon/states/{0}/committees")]
    public async Task Pre_electra_state_is_501_with_the_fixed_capability_message_not_a_bare_500(string routeTemplate)
    {
        ulong preElectraSlot = (Spec.ElectraForkEpoch - 1) * Presets.SlotsPerEpoch;
        Hash256 root = TestRoot(21);
        _host.Store.PutState(root, StateBytesForSlot(preElectraSlot));
        _host.Store.SetCanonicalRoot(preElectraSlot, root);

        HttpResponseMessage response = await _host.Client.GetAsync(string.Format(routeTemplate, preElectraSlot));
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(501), $"a fork this driver cannot decode is a labelled capability gap, not a 500; body: {raw}");
        JsonDocument body = JsonDocument.Parse(raw);
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(501));
        string message = body.RootElement.GetProperty("message").GetString()!;
        Assert.That(message, Is.EqualTo(BeaconApiEndpoints.UnsupportedForkMessage), "the caller gets the fixed sentence, not BeaconStateCodec's exception text");
        Assert.That(message, Does.Not.Contain("belongs to the"), "BeaconStateCodec's own wording must not leak");
    }

    [TestCase("/eth/v1/beacon/states/{0}/validators")]
    [TestCase("/eth/v1/beacon/states/{0}/validator_balances")]
    [TestCase("/eth/v1/beacon/states/{0}/committees")]
    [TestCase("/eth/v1/beacon/genesis")]
    [TestCase("/eth/v1/beacon/headers?slot=bad")]
    [TestCase("/eth/v1/beacon/headers/bad")]
    [TestCase("/eth/v1/beacon/blocks/bad/root")]
    [TestCase("/eth/v2/beacon/blocks/bad/attestations")]
    [TestCase("/eth/v1/beacon/states/bad/randao?epoch=bad")]
    [TestCase("/eth/v1/beacon/states/bad/sync_committees?epoch=bad")]
    [TestCase("/eth/v1/beacon/states/bad/fork")]
    [TestCase("/eth/v1/beacon/states/bad/root")]
    [TestCase("/eth/v1/beacon/states/bad/finality_checkpoints")]
    [TestCase("/eth/v1/beacon/states/bad/validators/bad")]
    [TestCase("/eth/v1/config/deposit_contract")]
    [TestCase("/eth/v1/config/spec")]
    [TestCase("/eth/v1/config/fork_schedule")]
    [TestCase("/eth/v1/debug/beacon/fork_choice")]
    [TestCase("/eth/v1/debug/fork_choice")]
    [TestCase("/eth/v1/node/identity")]
    [TestCase("/eth/v1/node/syncing")]
    [TestCase("/eth/v1/node/peer_count")]
    [TestCase("/eth/v1/node/peers?state=bad")]
    [TestCase("/eth/v1/node/peers/bad")]
    public async Task Json_only_endpoints_answer_406_for_an_octet_stream_only_accept_header(string routeTemplate)
    {
        // A Fulu-fork slot: these requests must fail on content negotiation, not on fork support.
        ulong fuluSlot = (Spec.GloasForkEpoch - 1) * Presets.SlotsPerEpoch;
        Hash256 root = TestRoot(22);
        _host.Store.PutState(root, MinimalFuluState(fuluSlot));

        HttpRequestMessage request = new(HttpMethod.Get, string.Format(routeTemplate, root));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        HttpResponseMessage response = await _host.Client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(406),
            "these endpoints serve JSON only (no octet-stream variant in the beacon-api spec for validators/validator_balances/committees); " +
            "claiming SSZ support the endpoint does not have, or silently serving JSON regardless of Accept, are both dishonest");
    }

    [Test]
    public async Task Post_negotiation_precedes_invalid_state_and_malformed_body(
        [Values("validators", "validator_balances")] string operation, [Values("application/json", "text/plain")] string contentType)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/eth/v1/beacon/states/bad/{operation}")
        {
            Content = new StringContent("{", System.Text.Encoding.UTF8, contentType)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using HttpResponseMessage response = await _host.Client.SendAsync(request);
        Assert.That((int)response.StatusCode, Is.EqualTo(406));
    }

    private static byte[] MinimalFuluState(ulong slot)
    {
        BeaconStateFulu state = MinimalState(Spec, slot,
            new Fork { PreviousVersion = [5, 0, 0, 0], CurrentVersion = [6, 0, 0, 0], Epoch = 0 },
            [], [], new Hash256[(int)Presets.EpochsPerHistoricalVector]);
        Array.Fill(state.RandaoMixes!, Hash256.Zero);
        return BeaconStateFulu.Encode(state);
    }

}
