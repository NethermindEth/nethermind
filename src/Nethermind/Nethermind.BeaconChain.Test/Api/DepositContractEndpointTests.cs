// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

public class DepositContractEndpointTests
{
    private const string Endpoint = "/eth/v1/config/deposit_contract";

    // Values from consensus-specs v1.7.0-beta.2 configs/mainnet.yaml and eth-clients sepolia and hoodi metadata/config.yaml.
    private static IEnumerable<TestCaseData> Networks()
    {
        yield return new TestCaseData(BeaconChainSpec.Mainnet, "1", "0x00000000219ab540356cBB839Cbe05303d7705Fa").SetArgDisplayNames("mainnet");
        yield return new TestCaseData(BeaconChainSpec.Sepolia, "11155111", "0x7f02C3E3c98b133055B8B348B2Ac625669Ed295D").SetArgDisplayNames("sepolia");
        yield return new TestCaseData(BeaconChainSpec.Hoodi, "560048", "0x00000000219ab540356cBB839Cbe05303d7705Fa").SetArgDisplayNames("hoodi");
    }

    [TestCaseSource(nameof(Networks))]
    public async Task Answers_the_network_deposit_contract(BeaconChainSpec spec, string chainId, string address)
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(spec);

        HttpResponseMessage response = await host.GetAsync(Endpoint, "application/json");
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(data.GetProperty("chain_id").ValueKind, Is.EqualTo(JsonValueKind.String), "Uint64 is a decimal string on the wire");
        Assert.That(data.GetProperty("chain_id").GetString(), Is.EqualTo(chainId));
        Assert.That(data.GetProperty("address").GetString(), Is.EqualTo(address));
    }

    [TestCase("application/octet-stream")]
    [TestCase("text/html")]
    public async Task Refuses_a_media_type_it_cannot_answer_in(string accept)
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);

        HttpResponseMessage response = await host.GetAsync(Endpoint, accept);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable), "the route has no SSZ form, so it must not answer JSON to a client that refuses it");
    }

    [Test]
    public async Task Refuses_rather_than_invents_an_address_for_a_chain_with_no_network_config()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        BeaconChainSpec adHoc = new()
        {
            SecondsPerSlot = mainnet.SecondsPerSlot,
            SlotsPerEpoch = mainnet.SlotsPerEpoch,
            GenesisTime = mainnet.GenesisTime,
            GenesisValidatorsRoot = mainnet.GenesisValidatorsRoot,
            Forks = mainnet.Forks,
            BlobSchedule = mainnet.BlobSchedule,
            ElectraForkEpoch = mainnet.ElectraForkEpoch,
            FuluForkEpoch = mainnet.FuluForkEpoch,
            MaxBlobsPerBlockElectra = mainnet.MaxBlobsPerBlockElectra,
            GloasForkEpoch = mainnet.GloasForkEpoch,
            GloasForkVersion = mainnet.GloasForkVersion,
            Bootnodes = mainnet.Bootnodes,
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(adHoc);

        HttpResponseMessage response = await host.GetAsync(Endpoint, "application/json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError), "the spec lists only 200 and 500 for this route");
    }
}
