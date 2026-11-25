// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core.Crypto;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

public class ValidatorEndpointsTests : BeaconApiFixture
{
    private static readonly BlsPublicKey Key = FilledPubkey(0xab);
    private static readonly BlsSignature Signature = FilledSignature(0xef);
    private static readonly Hash256 Root = FilledHash(0xcd);
    private static readonly Hash256 OtherRoot = FilledHash(0xce);

    private readonly IBeaconNodeValidatorApi _api = Substitute.For<IBeaconNodeValidatorApi>();

    [OneTimeSetUp]
    public async Task StartHost() => _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, validatorApi: _api);

    [SetUp]
    public void ResetApi() => _api.ClearSubstitute();

    private static IEnumerable<TestCaseData> ServedRoutes()
    {
        yield return new TestCaseData(HttpMethod.Post, "/eth/v1/validator/duties/attester/5", """["1","2"]""",
            (Action<IBeaconNodeValidatorApi>)(api => api.GetAttesterDuties(5, Arg.Is<IReadOnlyList<ulong>>(indices => indices.SequenceEqual(new ulong[] { 1, 2 })), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<DutiesResult<AttesterDuty>>(new DutiesResult<AttesterDuty>(Root, true, [new AttesterDuty(Key, 2, 3, 4, 5, 6, 7)])))),
            $$"""{"dependent_root":"{{Root}}","execution_optimistic":true,"data":[{"pubkey":"{{Key}}","validator_index":"2","committee_index":"3","committee_length":"4","committees_at_slot":"5","validator_committee_index":"6","slot":"7"}]}""")
            .SetName("Attester duties");
        yield return new TestCaseData(HttpMethod.Get, "/eth/v2/validator/duties/proposer/5", null,
            (Action<IBeaconNodeValidatorApi>)(api => api.GetProposerDuties(5, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<DutiesResult<ProposerDuty>>(new DutiesResult<ProposerDuty>(Root, false, [new ProposerDuty(Key, 9, 160)])))),
            $$"""{"dependent_root":"{{Root}}","execution_optimistic":false,"data":[{"pubkey":"{{Key}}","validator_index":"9","slot":"160"}]}""")
            .SetName("Proposer duties");
        yield return new TestCaseData(HttpMethod.Post, "/eth/v1/validator/duties/sync/256", """["1"]""",
            (Action<IBeaconNodeValidatorApi>)(api => api.GetSyncDuties(256, Arg.Is<IReadOnlyList<ulong>>(indices => indices.SequenceEqual(new ulong[] { 1 })), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<DutiesResult<SyncDuty>>(new DutiesResult<SyncDuty>(null, false, [new SyncDuty(Key, 1, [0, 511])])))),
            $$"""{"execution_optimistic":false,"data":[{"pubkey":"{{Key}}","validator_index":"1","validator_sync_committee_indices":["0","511"]}]}""")
            .SetName("Sync duties carry no dependent root");
        yield return new TestCaseData(HttpMethod.Get, "/eth/v1/validator/attestation_data?slot=7&committee_index=3", null,
            (Action<IBeaconNodeValidatorApi>)(api => api.ProduceAttestationData(7, 3, Arg.Any<CancellationToken>()).Returns(new ValueTask<AttestationData>(Data()))),
            $$$$"""{"data":{"slot":"7","index":"0","beacon_block_root":"{{{{Root}}}}","source":{"epoch":"1","root":"{{{{Root}}}}"},"target":{"epoch":"2","root":"{{{{OtherRoot}}}}"}}}""")
            .SetName("Attestation data");
        yield return new TestCaseData(HttpMethod.Get, $"/eth/v2/validator/aggregate_attestation?attestation_data_root={OtherRoot}&slot={FuluSlot}&committee_index=2", null,
            (Action<IBeaconNodeValidatorApi>)(api => api.GetAggregateAttestation(FuluSlot, OtherRoot, 2, Arg.Any<CancellationToken>()).Returns(new ValueTask<Attestation>(Aggregate()))),
            $$$$"""{"version":"fulu","data":{"aggregation_bits":"0x0d","data":{"slot":"7","index":"0","beacon_block_root":"{{{{Root}}}}","source":{"epoch":"1","root":"{{{{Root}}}}"},"target":{"epoch":"2","root":"{{{{OtherRoot}}}}"}},"signature":"{{{{Signature}}}}","committee_bits":"0x0400000000000000"}}""")
            .SetName("Aggregate attestation, versioned by the fork of its slot");
    }

    [TestCaseSource(nameof(ServedRoutes))]
    public async Task Served_routes_decode_the_request_and_encode_the_api_result(HttpMethod method, string path, string? body, Action<IBeaconNodeValidatorApi> arrange, string expected)
    {
        arrange(_api);

        using HttpResponseMessage response = await Send(method, path, body, consensusVersion: null);

        Assert.That(await ReadSuccessfulBodyAsync(response), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> Submissions()
    {
        const string data = """{"slot":"7","index":"0","beacon_block_root":"0xcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd","source":{"epoch":"1","root":"0xcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"},"target":{"epoch":"2","root":"0xcececececececececececececececececececececececececececececececece"}}""";
        foreach (bool refused in new[] { false, true })
        {
            yield return new TestCaseData("/eth/v2/beacon/pool/attestations", $$"""{"committee_index":"3","attester_index":"9","data":{{data}},"signature":"{{Signature}}"}""",
                (Func<IBeaconNodeValidatorApi, IReadOnlyList<SubmissionFailure>, Func<byte[][]>>)((api, failures) =>
                {
                    byte[][] submitted = [];
                    api.SubmitAttestations(Arg.Do<IReadOnlyList<SingleAttestation>>(attestations => submitted = [.. attestations.Select(SingleAttestation.Encode)]), Arg.Any<CancellationToken>())
                        .Returns(new ValueTask<IReadOnlyList<SubmissionFailure>>(failures));
                    return () => submitted;
                }),
                SingleAttestation.Encode(new SingleAttestation { CommitteeIndex = 3, AttesterIndex = 9, Data = Data(), Signature = Signature }), "Some attestations were not published.", refused)
                .SetName($"Pool attestations, {(refused ? "one refused" : "all published")}");
            yield return new TestCaseData("/eth/v2/validator/aggregate_and_proofs",
                $$"""{"message":{"aggregator_index":"9","aggregate":{"aggregation_bits":"0x0d","data":{{data}},"signature":"{{Signature}}","committee_bits":"0x0400000000000000"},"selection_proof":"{{OtherSignature}}"},"signature":"{{Signature}}"}""",
                (Func<IBeaconNodeValidatorApi, IReadOnlyList<SubmissionFailure>, Func<byte[][]>>)((api, failures) =>
                {
                    byte[][] submitted = [];
                    api.SubmitAggregateAndProofs(Arg.Do<IReadOnlyList<SignedAggregateAndProof>>(aggregates => submitted = [.. aggregates.Select(SignedAggregateAndProof.Encode)]), Arg.Any<CancellationToken>())
                        .Returns(new ValueTask<IReadOnlyList<SubmissionFailure>>(failures));
                    return () => submitted;
                }),
                SignedAggregateAndProof.Encode(new SignedAggregateAndProof
                {
                    Message = new AggregateAndProof { AggregatorIndex = 9, Aggregate = Aggregate(), SelectionProof = OtherSignature },
                    Signature = Signature,
                }), "Some aggregates were not published.", refused)
                .SetName($"Aggregate and proofs, {(refused ? "one refused" : "all published")}");
        }
    }

    [TestCaseSource(nameof(Submissions))]
    public async Task Submissions_are_decoded_and_the_refused_ones_reported_by_index(string path, string item, Func<IBeaconNodeValidatorApi, IReadOnlyList<SubmissionFailure>, Func<byte[][]>> arrange,
        byte[] expected, string message, bool refused)
    {
        Func<byte[][]> submitted = arrange(_api, refused ? [new SubmissionFailure(1, "The signature is invalid.")] : []);

        using HttpResponseMessage response = await Send(HttpMethod.Post, path, $"[{item},{item}]", consensusVersion: "fulu");

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((response.StatusCode, await response.Content.ReadAsStringAsync()), Is.EqualTo(refused
            ? (HttpStatusCode.BadRequest, $$"""{"code":400,"message":"{{message}}","failures":[{"index":1,"message":"The signature is invalid."}]}""")
            : (HttpStatusCode.OK, "")));
        Assert.That(submitted(), Is.EqualTo(new[] { expected, expected }));
    }

    [Test]
    public async Task Committee_subscriptions_are_decoded_and_passed_on()
    {
        IReadOnlyList<BeaconCommitteeSubscription> subscribed = [];
        _api.SubscribeToBeaconCommittees(Arg.Do<IReadOnlyList<BeaconCommitteeSubscription>>(subscriptions => subscribed = subscriptions), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        using HttpResponseMessage response = await Send(HttpMethod.Post, "/eth/v1/validator/beacon_committee_subscriptions",
            """[{"validator_index":"1","committee_index":"2","committees_at_slot":"3","slot":"4","is_aggregator":true},{"validator_index":"5","committee_index":"0","committees_at_slot":"1","slot":"6","is_aggregator":false}]""",
            consensusVersion: null);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(subscribed, Is.EqualTo(new[] { new BeaconCommitteeSubscription(1, 2, 3, 4, IsAggregator: true), new BeaconCommitteeSubscription(5, 0, 1, 6, IsAggregator: false) }));
    }

    [TestCase("POST", "/eth/v1/validator/duties/attester/x", """["1"]""", null, TestName = "An epoch that is not a uint64")]
    [TestCase("POST", "/eth/v1/validator/duties/attester/5", """["-1"]""", null, TestName = "A validator index that is not a uint64")]
    [TestCase("POST", "/eth/v1/validator/duties/sync/5", "[1]", null, TestName = "Validator indices that are not strings")]
    [TestCase("GET", "/eth/v1/validator/attestation_data", null, null, TestName = "Attestation data without a slot")]
    [TestCase("GET", "/eth/v1/validator/attestation_data?slot=1&committee_index=x", null, null, TestName = "Attestation data with a committee index that is not a uint64")]
    [TestCase("POST", "/eth/v2/beacon/pool/attestations", "[]", null, TestName = "Attestations without a consensus version")]
    [TestCase("POST", "/eth/v2/beacon/pool/attestations", "[]", "deneb", TestName = "Attestations of a fork before SingleAttestation")]
    [TestCase("POST", "/eth/v2/beacon/pool/attestations", """[{"committee_index":"1"}]""", "fulu", TestName = "An attestation missing its fields")]
    [TestCase("POST", "/eth/v1/validator/beacon_committee_subscriptions", """[{"validator_index":"1","slot":"4"}]""", null, TestName = "A committee subscription missing its fields")]
    [TestCase("GET", "/eth/v2/validator/aggregate_attestation?slot=1&committee_index=0", null, null, TestName = "An aggregate attestation without a data root")]
    [TestCase("GET", "/eth/v2/validator/aggregate_attestation?attestation_data_root=0xcd&slot=1&committee_index=0", null, null, TestName = "An aggregate attestation with a short data root")]
    [TestCase("POST", "/eth/v2/validator/aggregate_and_proofs", "[]", null, TestName = "Aggregates without a consensus version")]
    [TestCase("POST", "/eth/v2/validator/aggregate_and_proofs", """[{"message":{"aggregator_index":"1"},"signature":"0x00"}]""", "fulu", TestName = "An aggregate missing its fields")]
    public async Task Malformed_requests_are_refused_before_the_api_is_called(string method, string path, string? body, string? consensusVersion)
    {
        using HttpResponseMessage response = await Send(new HttpMethod(method), path, body, consensusVersion);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.That(_api.ReceivedCalls(), Is.Empty);
    }

    [TestCase(BeaconNodeApiError.InvalidRequest, HttpStatusCode.BadRequest)]
    [TestCase(BeaconNodeApiError.NotFound, HttpStatusCode.NotFound)]
    [TestCase(BeaconNodeApiError.NotSupported, HttpStatusCode.NotImplemented)]
    [TestCase(BeaconNodeApiError.NotReady, HttpStatusCode.ServiceUnavailable)]
    public async Task Api_refusals_answer_with_their_status_and_message(BeaconNodeApiError error, HttpStatusCode expected)
    {
        _api.GetProposerDuties(5, Arg.Any<CancellationToken>()).Throws(new BeaconNodeApiException(error, "refused for the test"));

        using HttpResponseMessage response = await _host.Client.GetAsync("/eth/v2/validator/duties/proposer/5");

        Assert.That((response.StatusCode, await response.Content.ReadAsStringAsync()), Is.EqualTo((expected, $$"""{"code":{{(int)expected}},"message":"refused for the test"}""")));
    }

    private static readonly ulong FuluSlot = BeaconChainSpec.Mainnet.FuluForkEpoch * BeaconChainSpec.Mainnet.SlotsPerEpoch;
    private static readonly BlsSignature OtherSignature = FilledSignature(0xf0);

    /// <summary>An aggregate of committee 2, whose members 0 and 2 of 3 attested.</summary>
    private static Attestation Aggregate() => new()
    {
        AggregationBits = new BitArray(3) { [0] = true, [2] = true },
        Data = Data(),
        Signature = Signature,
        CommitteeBits = new BitArray(64) { [2] = true },
    };

    private static AttestationData Data() => new()
    {
        Slot = 7,
        BeaconBlockRoot = Root,
        Source = new Checkpoint { Epoch = 1, Root = Root },
        Target = new Checkpoint { Epoch = 2, Root = OtherRoot },
    };

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body, string? consensusVersion)
    {
        using HttpRequestMessage request = new(method, path) { Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json") };
        if (consensusVersion is not null) request.Headers.Add("Eth-Consensus-Version", consensusVersion);
        return await _host.Client.SendAsync(request);
    }
}
