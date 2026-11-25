// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorClient;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.ValidatorClient;

/// <remarks>
/// The requests and response are the <c>ATTESTATION</c>, <c>AGGREGATION_SLOT</c>, <c>AGGREGATE_AND_PROOF_V2 (FULU)</c>, <c>SYNC_COMMITTEE_MESSAGE</c>,
/// <c>SYNC_COMMITTEE_SELECTION_PROOF</c> and <c>SYNC_COMMITTEE_CONTRIBUTION_AND_PROOF</c> examples of the Web3Signer eth2 signing OpenAPI
/// (Consensys/web3signer <c>openapi-specs/eth2/signing/paths/sign.yaml</c>), so the signing roots of all but the aggregate are checked against
/// the ones Web3Signer published for the same object, fork version and genesis validators root. The root published with the aggregate example
/// does not follow from its object under the Electra containers; that case expects the root of a separate SSZ computation. The contribution
/// example is of the minimal preset, whose 8-bit subcommittee bitvector packs into the same chunk as the mainnet 128-bit one, so its root holds.
/// </remarks>
public class Web3SignerClientTests
{
    private const string PublicKey = "0x989d34725a2bfc3f15105f3f5fc8741f436c25ee1ee4f948e425d6bcb8c56bce6e06c269635b7e985a7ffa639e2409bf";
    private const string Signature = "0xb3baa751d0a9132cfe93e4e3d5ff9075111100e3789dca219ade5a24d27e19d16b3353149da1833e9b691bb38634e8dc04469be7032132906c927d7e1a49b414730612877bc6b2810c8f202daf793d1ab0d6b5cb21d52f9e52e883859887a5d9";
    private static readonly Hash256 BlockRoot = new("0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89");
    private static readonly Hash256 SyncBlockRoot = new("0x235bc3400c2839fd856a524871200bd5e362db615fc4565e1870ed9a2a936464");

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet with
    {
        GenesisValidatorsRoot = new Hash256("0x04700007fabc8282644aed6d1c7c9e21d38a03a0c4ba193f3afe428824b3a673"),
        Forks = [new ForkScheduleEntry(Bytes.FromHexString("0x00000001"), 0)],
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
    };

    private const string ForkInfo = """{"fork":{"previous_version":"0x00000001","current_version":"0x00000001","epoch":"0"},"genesis_validators_root":"0x04700007fabc8282644aed6d1c7c9e21d38a03a0c4ba193f3afe428824b3a673"}""";

    private static readonly AttestationData Data = new()
    {
        Slot = 32,
        Index = 0,
        BeaconBlockRoot = BlockRoot,
        Source = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        Target = new Checkpoint { Epoch = 0, Root = BlockRoot },
    };

    public enum Answer { SlashingProtection, UnknownKey, BadRequest, ServerError, NoSignature, Unreachable, Timeout, CallerCancels }

    private static IEnumerable<TestCaseData> SigningRequests()
    {
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignAttestation(key, Data, CancellationToken.None)),
            $$$$"""{"type":"ATTESTATION","fork_info":{{{{ForkInfo}}}},"signingRoot":"0x548c9a015f4c96cb8b1ddbbdfca85846f85bf9f344a434c140f378cdfb5341f0","attestation":{"slot":"32","index":"0","beacon_block_root":"0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89","source":{"epoch":"0","root":"0x0000000000000000000000000000000000000000000000000000000000000000"},"target":{"epoch":"0","root":"0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89"}}}""")
            .SetName("ATTESTATION");
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignAggregationSlot(key, 119, CancellationToken.None)),
            $$$$"""{"type":"AGGREGATION_SLOT","fork_info":{{{{ForkInfo}}}},"signingRoot":"0x1fb90dd6e8b2670e6949347bc4eaacd37f9b6cc6e42c559973e362c800e853b9","aggregation_slot":{"slot":"119"}}""")
            .SetName("AGGREGATION_SLOT");
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignAggregateAndProof(key, AggregateAndProof(), CancellationToken.None)),
            $$$$"""{"type":"AGGREGATE_AND_PROOF_V2","fork_info":{{{{ForkInfo}}}},"signingRoot":"0x1180bcb41bda522b3420274c6e4931c37ecf4eef99a57e4d04fc81e82b2ced40","aggregate_and_proof":{"version":"FULU","data":{"aggregator_index":"1","aggregate":{"aggregation_bits":"0x0000000000000000000000000000000000000000000101","data":{"slot":"0","index":"0","beacon_block_root":"0x100814c335d0ced5014cfa9d2e375e6d9b4e197381f8ce8af0473200fdc917fd","source":{"epoch":"0","root":"0x0000000000000000000000000000000000000000000000000000000000000000"},"target":{"epoch":"0","root":"0x100814c335d0ced5014cfa9d2e375e6d9b4e197381f8ce8af0473200fdc917fd"}},"signature":"0xa627242e4a5853708f4ebf923960fb8192f93f2233cd347e05239d86dd9fb66b721ceec1baeae6647f498c9126074f1101a87854d674b6eebc220fd8c3d8405bdfd8e286b707975d9e00a56ec6cbbf762f23607d490f0bbb16c3e0e483d51875","committee_bits":"0x0000000000000001"},"selection_proof":"0xa63f73a03f1f42b1fd0a988b614d511eb346d0a91c809694ef76df5ae021f0f144d64e612d735bc8820950cf6f7f84cd0ae194bfe3d4242fe79688f83462e3f69d9d33de71aab0721b7dab9d6960875e5fdfd26b171a75fb51af822043820c47"}}}""")
            .SetName("AGGREGATE_AND_PROOF_V2");
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignSyncCommitteeMessage(key, 0, SyncBlockRoot, CancellationToken.None)),
            $$$$"""{"type":"SYNC_COMMITTEE_MESSAGE","fork_info":{{{{ForkInfo}}}},"signingRoot":"0xa6f60df2817ea5b52eed1fefebbad746ef64c6249fc05c90c9e0f520cc75bb95","sync_committee_message":{"beacon_block_root":"0x235bc3400c2839fd856a524871200bd5e362db615fc4565e1870ed9a2a936464","slot":"0"}}""")
            .SetName("SYNC_COMMITTEE_MESSAGE");
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignSyncCommitteeSelectionProof(key, 0, 0, CancellationToken.None)),
            $$$$"""{"type":"SYNC_COMMITTEE_SELECTION_PROOF","fork_info":{{{{ForkInfo}}}},"signingRoot":"0x50d85c783ab27c1eb3f3efa914b91cb93ffd677137b15c27ba5bb548306e6963","sync_aggregator_selection_data":{"slot":"0","subcommittee_index":"0"}}""")
            .SetName("SYNC_COMMITTEE_SELECTION_PROOF");
        yield return new TestCaseData((Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>>)((signer, key) => signer.SignContributionAndProof(key, ContributionAndProof(), CancellationToken.None)),
            $$$$"""{"type":"SYNC_COMMITTEE_CONTRIBUTION_AND_PROOF","fork_info":{{{{ForkInfo}}}},"signingRoot":"0xae94702468b584a3b1c422bc1b39cc523d9175ba3b9ac1cccb699c00507cc1a5","contribution_and_proof":{"aggregator_index":"11","selection_proof":"0x8f5c34de9e22ceaa7e8d165fc0553b32f02188539e89e2cc91e2eb9077645986550d872ee3403204ae5d554eae3cac12124e18d2324bccc814775316aaef352abc0450812b3ca9fde96ecafa911b3b8bfddca8db4027f08e29c22a9c370ad933","contribution":{"slot":"0","beacon_block_root":"0x235bc3400c2839fd856a524871200bd5e362db615fc4565e1870ed9a2a936464","subcommittee_index":"1","aggregation_bits":"0x24000000000000000000000000000000","signature":"0x9005ed0936f527d416609285b355fe6b9610d730c18b9d2f4942ba7d0eb95ba304ff46b6a2fb86f0c756bf09274db8e11399b7642f9fc5ae50b5bd9c1d87654277a19bfc3df78d36da16f44a48630d9550774a4ca9f3a5b55bbf33345ad2ec71"}}}""")
            .SetName("SYNC_COMMITTEE_CONTRIBUTION_AND_PROOF");
    }

    [TestCaseSource(nameof(SigningRequests))]
    public async Task Posts_the_object_with_its_fork_info_and_signing_root_and_reads_the_signature(Func<Web3SignerClient, BlsPublicKey, ValueTask<BlsSignature>> sign, string expectedBody)
    {
        string? url = null, accept = null, body = null;
        using Web3SignerClient signer = Signer(async (request, cancellationToken) =>
        {
            (url, accept, body) = (request.RequestUri!.ToString(), request.Headers.Accept.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken));
            return Respond(HttpStatusCode.OK, $$"""{"signature":"{{Signature}}"}""");
        }, TimeSpan.FromMinutes(1));

        BlsSignature signature = await sign(signer, new BlsPublicKey(Bytes.FromHexString(PublicKey)));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(url, Is.EqualTo($"http://signer.test:9000/api/v1/eth2/sign/{PublicKey}"));
        Assert.That(accept, Is.EqualTo("application/json"));
        Assert.That(body, Is.EqualTo(expectedBody));
        Assert.That(signature, Is.EqualTo(new BlsSignature(Bytes.FromHexString(Signature))));
    }

    /// <param name="refused">Whether the signer declined, or <c>null</c> when the caller's cancellation propagates instead of a signer failure.</param>
    [TestCase(Answer.SlashingProtection, true)]
    [TestCase(Answer.UnknownKey, true)]
    [TestCase(Answer.BadRequest, false)]
    [TestCase(Answer.ServerError, false)]
    [TestCase(Answer.NoSignature, false)]
    [TestCase(Answer.Unreachable, false)]
    [TestCase(Answer.Timeout, false)]
    [TestCase(Answer.CallerCancels, null)]
    public void A_signature_not_produced_is_a_signer_failure_refused_only_when_the_signer_declined(Answer answer, bool? refused)
    {
        using CancellationTokenSource caller = new();
        using Web3SignerClient signer = Signer(async (_, cancellationToken) =>
        {
            switch (answer)
            {
                case Answer.Unreachable:
                    throw new HttpRequestException("Connection refused");
                case Answer.Timeout or Answer.CallerCancels:
                    if (answer is Answer.CallerCancels) await caller.CancelAsync();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
            }

            return answer switch
            {
                Answer.SlashingProtection => Respond(HttpStatusCode.PreconditionFailed, ""),
                Answer.UnknownKey => Respond(HttpStatusCode.NotFound, ""),
                Answer.BadRequest => Respond(HttpStatusCode.BadRequest, ""),
                Answer.ServerError => Respond(HttpStatusCode.InternalServerError, ""),
                _ => Respond(HttpStatusCode.OK, "{}"),
            };
        }, requestTimeout: answer is Answer.Timeout ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMinutes(1));

        Assert.That(async () => await signer.SignAttestation(new BlsPublicKey(Bytes.FromHexString(PublicKey)), Data, caller.Token), refused is null
            ? Throws.InstanceOf<OperationCanceledException>()
            : Throws.TypeOf<ValidatorSignerException>().With.Property(nameof(ValidatorSignerException.Refused)).EqualTo(refused));
    }

    /// <summary>The <c>AGGREGATE_AND_PROOF_V2 (FULU)</c> example's aggregate: committee 56, whose member at position 168 of 176 attests.</summary>
    private static AggregateAndProof AggregateAndProof()
    {
        Hash256 blockRoot = new("0x100814c335d0ced5014cfa9d2e375e6d9b4e197381f8ce8af0473200fdc917fd");
        return new AggregateAndProof
        {
            AggregatorIndex = 1,
            Aggregate = new Attestation
            {
                AggregationBits = new BitArray(176) { [168] = true },
                Data = new AttestationData { BeaconBlockRoot = blockRoot, Source = new Checkpoint { Root = Hash256.Zero }, Target = new Checkpoint { Root = blockRoot } },
                Signature = new BlsSignature(Bytes.FromHexString("0xa627242e4a5853708f4ebf923960fb8192f93f2233cd347e05239d86dd9fb66b721ceec1baeae6647f498c9126074f1101a87854d674b6eebc220fd8c3d8405bdfd8e286b707975d9e00a56ec6cbbf762f23607d490f0bbb16c3e0e483d51875")),
                CommitteeBits = new BitArray(64) { [56] = true },
            },
            SelectionProof = new BlsSignature(Bytes.FromHexString("0xa63f73a03f1f42b1fd0a988b614d511eb346d0a91c809694ef76df5ae021f0f144d64e612d735bc8820950cf6f7f84cd0ae194bfe3d4242fe79688f83462e3f69d9d33de71aab0721b7dab9d6960875e5fdfd26b171a75fb51af822043820c47")),
        };
    }

    /// <summary>The <c>SYNC_COMMITTEE_CONTRIBUTION_AND_PROOF</c> example's contribution: members 2 and 5 of subcommittee 1 signed.</summary>
    private static ContributionAndProof ContributionAndProof() => new()
    {
        AggregatorIndex = 11,
        Contribution = new SyncCommitteeContribution
        {
            BeaconBlockRoot = SyncBlockRoot,
            SubcommitteeIndex = 1,
            AggregationBits = new BitArray(SyncCommitteeContribution.SubcommitteeSize) { [2] = true, [5] = true },
            Signature = new BlsSignature(Bytes.FromHexString("0x9005ed0936f527d416609285b355fe6b9610d730c18b9d2f4942ba7d0eb95ba304ff46b6a2fb86f0c756bf09274db8e11399b7642f9fc5ae50b5bd9c1d87654277a19bfc3df78d36da16f44a48630d9550774a4ca9f3a5b55bbf33345ad2ec71")),
        },
        SelectionProof = new BlsSignature(Bytes.FromHexString("0x8f5c34de9e22ceaa7e8d165fc0553b32f02188539e89e2cc91e2eb9077645986550d872ee3403204ae5d554eae3cac12124e18d2324bccc814775316aaef352abc0450812b3ca9fde96ecafa911b3b8bfddca8db4027f08e29c22a9c370ad933")),
    };

    private static Web3SignerClient Signer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan requestTimeout) =>
        new(new ValidatorClientConfig { RemoteSignerUrl = "http://signer.test:9000/" }, Spec)
        {
            Http = new HttpClient(new ScriptedHandler(respond)),
            RequestTimeout = requestTimeout,
        };

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
