// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http;
using System.Text.Json;

namespace Nethermind.BeaconChain.Test.Api;

public abstract class BeaconApiFixture
{
    private protected BeaconApiTestHost _host = null!;

    [OneTimeTearDown]
    public async Task DisposeHost() => await _host.DisposeAsync();

    private protected async Task<JsonElement> ReadFuluEnvelope(string path, bool expectedFinalized)
    {
        using HttpResponseMessage response = await _host.GetAsync(path, "application/json");
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));

        JsonElement root = JsonDocument.Parse(raw).RootElement;
        Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(root.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("finalized").GetBoolean(), Is.EqualTo(expectedFinalized));
        return root;
    }
}
