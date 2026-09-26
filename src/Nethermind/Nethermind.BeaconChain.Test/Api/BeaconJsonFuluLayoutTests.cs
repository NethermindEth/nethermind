// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>The Fulu block JSON shares writers with the Gloas block, so its bytes are pinned here: a shared writer that moves or changes a Fulu field fails.</summary>
public class BeaconJsonFuluLayoutTests
{
    private const ulong Slot = 412_500 * 32 + 7;

    private BeaconApiTestHost _host = null!;
    private string _raw = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await StartAsync(BeaconChainSpec.Mainnet);
        Hash256 root = TestRoot(0x10);
        _host.Store.PutBlock(root, RichBlock(Slot, FilledHash(0x00)));
        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", "application/json");
        _raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), _raw);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [Test]
    public void Block_body_fields_follow_the_fulu_container_order()
    {
        JsonElement body = JsonDocument.Parse(_raw).RootElement.GetProperty("data").GetProperty("message").GetProperty("body");

        // specs/electra/beacon-chain.md BeaconBlockBody, unchanged in specs/fulu.
        Assert.That(body.EnumerateObject().Select(static p => p.Name), Is.EqualTo(new[]
        {
            "randao_reveal", "eth1_data", "graffiti", "proposer_slashings", "attester_slashings", "attestations",
            "deposits", "voluntary_exits", "sync_aggregate", "execution_payload", "bls_to_execution_changes",
            "blob_kzg_commitments", "execution_requests",
        }));
        Assert.That(body.GetProperty("execution_requests").EnumerateObject().Select(static p => p.Name), Is.EqualTo(new[] { "deposits", "withdrawals", "consolidations" }));
    }

    [Test]
    public void Block_json_bytes_are_unchanged() =>
        Assert.That(Bytes.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_raw))), Is.EqualTo("8e53a2bbacd1dcb44aa240c0db316d1e857b52b57048b9b17440db063ec2e60e"), _raw);
}
