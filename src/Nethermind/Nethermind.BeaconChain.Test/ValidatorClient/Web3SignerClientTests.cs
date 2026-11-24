// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// The request and response are the <c>ATTESTATION</c> example of the Web3Signer eth2 signing OpenAPI
/// (Consensys/web3signer <c>openapi-specs/eth2/signing/paths/sign.yaml</c>), so the signing root computed here is
/// checked against the one Web3Signer published for the same object, fork version and genesis validators root.
/// </remarks>
public class Web3SignerClientTests
{
    private const string PublicKey = "0x989d34725a2bfc3f15105f3f5fc8741f436c25ee1ee4f948e425d6bcb8c56bce6e06c269635b7e985a7ffa639e2409bf";
    private const string Signature = "0xb3baa751d0a9132cfe93e4e3d5ff9075111100e3789dca219ade5a24d27e19d16b3353149da1833e9b691bb38634e8dc04469be7032132906c927d7e1a49b414730612877bc6b2810c8f202daf793d1ab0d6b5cb21d52f9e52e883859887a5d9";
    private static readonly Hash256 BlockRoot = new("0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89");

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet with
    {
        GenesisValidatorsRoot = new Hash256("0x04700007fabc8282644aed6d1c7c9e21d38a03a0c4ba193f3afe428824b3a673"),
        Forks = [new ForkScheduleEntry(Bytes.FromHexString("0x00000001"), 0)],
    };

    private static readonly AttestationData Data = new()
    {
        Slot = 32,
        Index = 0,
        BeaconBlockRoot = BlockRoot,
        Source = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        Target = new Checkpoint { Epoch = 0, Root = BlockRoot },
    };

    public enum Answer { SlashingProtection, UnknownKey, BadRequest, ServerError, NoSignature, Unreachable, Timeout, CallerCancels }

    [Test]
    public async Task Posts_the_attestation_with_its_fork_info_and_signing_root_and_reads_the_signature()
    {
        string? url = null, accept = null, body = null;
        using Web3SignerClient signer = Signer(async (request, cancellationToken) =>
        {
            (url, accept, body) = (request.RequestUri!.ToString(), request.Headers.Accept.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken));
            return Respond(HttpStatusCode.OK, $$"""{"signature":"{{Signature}}"}""");
        }, TimeSpan.FromMinutes(1));

        BlsSignature signature = await signer.SignAttestation(new BlsPublicKey(Bytes.FromHexString(PublicKey)), Data, CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(url, Is.EqualTo($"http://signer.test:9000/api/v1/eth2/sign/{PublicKey}"));
        Assert.That(accept, Is.EqualTo("application/json"));
        Assert.That(body, Is.EqualTo("""{"type":"ATTESTATION","fork_info":{"fork":{"previous_version":"0x00000001","current_version":"0x00000001","epoch":"0"},"genesis_validators_root":"0x04700007fabc8282644aed6d1c7c9e21d38a03a0c4ba193f3afe428824b3a673"},"signingRoot":"0x548c9a015f4c96cb8b1ddbbdfca85846f85bf9f344a434c140f378cdfb5341f0","attestation":{"slot":"32","index":"0","beacon_block_root":"0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89","source":{"epoch":"0","root":"0x0000000000000000000000000000000000000000000000000000000000000000"},"target":{"epoch":"0","root":"0xb2eedb01adbd02c828d5eec09b4c70cbba12ffffba525ebf48aca33028e8ad89"}}}"""));
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
