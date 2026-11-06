// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class CheckpointSourceTests
{
    private const string Root = "0x6ac776d12a0f63834f8ebac4882ecc4ee863b2f0f349a765ab2058c92eb18047";
    private const string Endpoint = "https://checkpoint-sync.hoodi.ethpandaops.io";
    private const string CrossCheck = "https://another-provider.example";
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
        "NethermindLightClientCheckpointTests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestCase("mainnet", null)]
    [TestCase("hoodi", "https://checkpoint-sync.hoodi.ethpandaops.io")]
    [TestCase("sepolia", "https://checkpoint-sync.sepolia.ethpandaops.io")]
    public void Only_ef_testnet_providers_are_defaults(string network, string? expected) =>
        Assert.That(CheckpointSource.DefaultUrl(network), Is.EqualTo(expected));

    [Test]
    public async Task Fetches_and_retains_the_original_provider_checkpoint_instead_of_refetching_on_restart()
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);
        CheckpointSelection selected = await Resolve(client);
        handler.Body = Finality("0x1111111111111111111111111111111111111111111111111111111111111111");
        handler.Requests.Clear();

        CheckpointSelection reused = await Resolve(client, currentSlot: 200_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.Root, Is.EqualTo(new Hash256(Root)));
            Assert.That(selected.Reused, Is.False);
            Assert.That(selected.Epoch, Is.EqualTo(10UL));
            Assert.That(selected.Provider, Is.EqualTo(Endpoint + "/"));
            Assert.That(reused, Is.EqualTo(selected with { Reused = true }));
            Assert.That(handler.Requests, Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }
    }

    [Test]
    public async Task Accepts_existing_checkpoint_services_that_omit_top_level_status_flags()
    {
        using ResponseHandler handler = new(JsonSerializer.Serialize(new { data = new { finalized = new { root = Root, epoch = "10" } } }));
        using HttpClient client = new(handler);

        Assert.That((await Resolve(client)).Root, Is.EqualTo(new Hash256(Root)));
    }

    [Test]
    public async Task Refuses_a_provider_change_without_silently_selecting_a_new_anchor()
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);
        await Resolve(client);
        handler.Requests.Clear();

        Assert.That(async () => await Resolve(client, endpoint: CrossCheck),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("different network or provider"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase("Version", "2")]
    [TestCase("Network", "\"sepolia\"")]
    [TestCase("Root", "\"0x0\"")]
    [TestCase("Epoch", "999999")]
    [TestCase("SelectedAt", "\"2000-01-01T00:00:00+00:00\"")]
    [TestCase("SelectedAt", "\"2100-01-01T00:00:00+00:00\"")]
    public async Task Rejects_modified_saved_provenance(string field, string replacement)
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);
        await Resolve(client);
        string path = Path.Combine(_directory, "hoodi-checkpoint.json");
        Dictionary<string, JsonElement> saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(path))!;
        saved[field] = JsonSerializer.Deserialize<JsonElement>(replacement);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(saved));
        handler.Requests.Clear();

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>());
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Existing_journal_without_provenance_requires_an_explicit_original_anchor()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "hoodi-OLDTRUSTEDROOT.chain"), "existing authenticated continuity");
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>().With.Message.Contains("original --checkpoint"));
        Assert.That(handler.Requests, Is.Empty);
        Assert.That(Directory.GetFiles(_directory, "*-checkpoint.json"), Is.Empty);
    }

    [Test]
    public async Task Rejects_invalid_saved_json_without_fetching_a_replacement([Values("{", "null")] string saved)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "hoodi-checkpoint.json"), saved);
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>());
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Cancelled_restart_preserves_the_saved_anchor()
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);
        await Resolve(client);
        string path = Path.Combine(_directory, "hoodi-checkpoint.json");
        byte[] before = await File.ReadAllBytesAsync(path);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Assert.That(async () => await Resolve(client, token: cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task Interrupted_temporary_provenance_does_not_become_a_trust_anchor()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "hoodi-checkpoint.json.interrupted.tmp"), "{\"Root\":\"uncommitted root\"");
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);

        CheckpointSelection selected = await Resolve(client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.Root, Is.EqualTo(new Hash256(Root)));
            Assert.That(selected.Reused, Is.False);
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(File.Exists(Path.Combine(_directory, "hoodi-checkpoint.json")), Is.True);
        }
    }

    [Test]
    public void Rejects_invalid_checkpoint_roots(
        [Values("0x0", "0x0000000000000000000000000000000000000000000000000000000000000000",
            "0x6ac776d12a0f63834f8ebac4882ecc4ee863b2f0f349a765ab2058c92eb1804x")] string root)
    {
        using ResponseHandler handler = new(Finality(root));
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.Exists(_directory), Is.False);
    }

    [Test]
    public void Rejects_invalid_provider_urls(
        [Values("http://example.org", "https://user:password@example.org", "https://example.org?token=secret", "https://example.org#fragment")] string endpoint)
    {
        using ResponseHandler handler = new("{}");
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client, endpoint: endpoint), Throws.TypeOf<ArgumentException>());
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void Rejects_invalid_finality_response(
        [Values("{}", "{\"data\":{\"finalized\":{\"root\":23}}}",
            "{\"finalized\":false,\"execution_optimistic\":false,\"data\":{}}",
            "{\"finalized\":true,\"execution_optimistic\":true,\"data\":{}}",
            "{\"finalized\":true,\"execution_optimistic\":false,\"data\":{\"finalized\":{\"root\":\"0x01\",\"epoch\":\"-1\"}}}", "[{}]")] string body)
    {
        using ResponseHandler handler = new(body);
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(320UL, true)]
    [TestCase(8512UL, true)]
    [TestCase(8544UL, false)]
    [TestCase(319UL, false)]
    public async Task Newly_selected_provider_epoch_must_meet_the_local_age_policy(ulong currentSlot, bool accepted)
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);

        if (accepted) Assert.That((await Resolve(client, currentSlot)).Epoch, Is.EqualTo(10UL));
        else Assert.That(async () => await Resolve(client, currentSlot), Throws.TypeOf<InvalidDataException>().With.Message.Contains("256-epoch"));
    }

    [Test]
    public void Rejects_the_wrong_provider_network()
    {
        using ResponseHandler handler = new(Finality()) { Network = BeaconChainSpec.Sepolia };
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<InvalidDataException>().With.Message.Contains("different beacon network"));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase(true, false, true, Root, true)]
    [TestCase(false, false, true, Root, false)]
    [TestCase(true, true, true, Root, false)]
    [TestCase(true, false, false, Root, false)]
    [TestCase(true, false, true, "0x1111111111111111111111111111111111111111111111111111111111111111", false)]
    public async Task Cross_check_requires_the_selected_root_to_be_canonical_finalized_and_nonoptimistic(
        bool finalized, bool optimistic, bool canonical, string root, bool accepted)
    {
        string response = JsonSerializer.Serialize(new { finalized, execution_optimistic = optimistic, data = new { root, canonical } });
        using ResponseHandler handler = new(Finality()) { CrossCheckBody = response };
        using HttpClient client = new(handler);

        if (accepted)
        {
            CheckpointSelection selected = await Resolve(client, crossCheck: CrossCheck);
            Assert.That(selected.Root, Is.EqualTo(new Hash256(Root)));
            Assert.That(handler.Requests.Last(), Is.EqualTo(CrossCheck + "/eth/v1/beacon/headers/" + Root));
        }
        else
        {
            Assert.That(async () => await Resolve(client, crossCheck: CrossCheck), Throws.TypeOf<InvalidDataException>());
            Assert.That(Directory.Exists(_directory), Is.False);
        }
    }

    [Test]
    public void Cross_check_requires_a_different_host()
    {
        using ResponseHandler handler = new(Finality());
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client, crossCheck: Endpoint + "/other"),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("different HTTPS provider host"));
        Assert.That(Directory.Exists(_directory), Is.False);
    }

    [Test]
    public void Rejects_non_success_responses([Values(HttpStatusCode.Redirect, HttpStatusCode.ServiceUnavailable)] HttpStatusCode status)
    {
        using ResponseHandler handler = new("{}") { Status = status };
        using HttpClient client = new(handler);

        Assert.That(async () => await Resolve(client), Throws.TypeOf<HttpRequestException>());
    }

    private Task<CheckpointSelection> Resolve(HttpClient client, ulong currentSlot = 320, string endpoint = Endpoint,
        string? crossCheck = null, CancellationToken token = default) =>
        CheckpointSource.ResolveAsync(client, endpoint, "hoodi", _directory, BeaconChainSpec.Hoodi, currentSlot, crossCheck, token);

    private static string Finality(string root = Root) => JsonSerializer.Serialize(new
    {
        finalized = true,
        execution_optimistic = false,
        data = new { finalized = new { root, epoch = "10" } },
    });

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal string Body { get; set; } = body;
        internal string CrossCheckBody { get; init; } = "{}";
        internal BeaconChainSpec Network { get; init; } = BeaconChainSpec.Hoodi;
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            string response = request.RequestUri.AbsolutePath.EndsWith("/genesis", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { data = new { genesis_validators_root = Network.GenesisValidatorsRoot.ToString(), genesis_time = Network.GenesisTime.ToString() } })
                : request.RequestUri.AbsolutePath.Contains("/headers/", StringComparison.Ordinal) ? CrossCheckBody : Body;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(response) });
        }
    }
}
