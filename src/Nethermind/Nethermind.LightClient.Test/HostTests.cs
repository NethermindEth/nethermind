// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Nethermind.LightClient.Test;

public class HostTests
{
    [Test]
    public async Task Browser_entry_paths_serve_the_index_without_redirect_loops(
        [Values("/browser", "/browser/", "/browser/index.html")] string path)
    {
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientBrowserHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "Nethermind browser");
            await using WebApplication app = CreateApp("http://127.0.0.1:0");
            BrowserHost.Configure(app, directory, null, "http://127.0.0.1:0");
            await app.StartAsync();
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(app.Urls.Single() + path);

            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Nethermind browser"));
            Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain("wasm-unsafe-eval"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase(null, null, null, 200)]
    [TestCase(null, "local", null, 200)]
    [TestCase(null, "https://wallet.example", null, 403)]
    [TestCase("https://wallet.example", "https://wallet.example", null, 200)]
    [TestCase(null, "http://attacker.example", "attacker.example", 403)]
    public async Task Browser_origin_and_host_policy_rejects_cross_site_and_rebinding_requests(
        string? allowedOrigin, string? requestOrigin, string? requestHost, int expectedStatus)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        BrowserHost.Configure(app, null, allowedOrigin, "http://127.0.0.1:0");
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single());
        if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin == "local" ? app.Urls.Single() : requestOrigin);
        if (requestHost is not null) request.Headers.Host = requestHost;
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == 200 && requestOrigin is not null)
            Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single(),
                Is.EqualTo(requestOrigin == "local" ? app.Urls.Single() : requestOrigin));
    }

    [Test]
    public void Browser_origin_requires_an_exact_secure_origin([Values("*", "http://wallet.example", "https://wallet.example/path", "https://wallet.example/?q=x")] string origin)
    {
        using WebApplication app = CreateApp("http://127.0.0.1:0");
        Assert.That(() => BrowserHost.Configure(app, null, origin, "http://127.0.0.1:0"), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Failed_listener_start_does_not_launch_consensus_work()
    {
        await using WebApplication occupying = CreateApp("http://127.0.0.1:0");
        await occupying.StartAsync();
        await using WebApplication app = CreateApp(occupying.Urls.Single());
        bool launched = false;

        Assert.CatchAsync<IOException>(async () => await LightClientHost.RunAsync(app, _ =>
        {
            launched = true;
            return Task.CompletedTask;
        }));

        Assert.That(launched, Is.False);
    }

    [Test]
    public async Task Host_waits_for_consensus_work_before_disposal([Values] bool failSync)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(app, async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.SetResult());
            ready.SetResult();
            await release.Task;
            if (failSync) throw new InvalidDataException("sync failure");
        });
        await ready.Task;
        app.Lifetime.StopApplication();
        await canceled.Task;
        Assert.That(run.IsCompleted, Is.False);
        release.SetResult();
        if (failSync)
            Assert.That(async () => await run, Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("sync failure"));
        else
            await run;
        Assert.That(() => app.Services.GetService(typeof(ILoggerFactory)), Throws.Nothing);
    }

    private static WebApplication CreateApp(string url)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        return builder.Build();
    }
}
