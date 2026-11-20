// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using ApplicationLifetime = Microsoft.Extensions.Hosting.Internal.ApplicationLifetime;
using SharedWebHost = Nethermind.JsonRpc.Hosting.WebHost;

namespace Nethermind.LightClient.Test;

public class KestrelConfigurationTests
{
    [Test]
    [CancelAfter(15_000)]
    public async Task Builder_preserves_configured_https_certificates_and_endpoints(
        [Values] bool configuredEndpoint, CancellationToken token)
    {
        const string Password = "light-client-kestrel-test";
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "nethermind-light-client-kestrel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            SubjectAlternativeNameBuilder names = new();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using X509Certificate2 certificate = request.CreateSelfSigned(
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await File.WriteAllBytesAsync(Path.Combine(directory, "server.pfx"), certificate.Export(X509ContentType.Pfx, Password), token);
            Dictionary<string, object> kestrel = new()
            {
                ["Certificates"] = new { Default = new { Path = "server.pfx", Password } },
            };
            if (configuredEndpoint) kestrel["Endpoints"] = new { Rpc = new { Url = "https://127.0.0.1:0" } };
            await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), JsonSerializer.Serialize(new { Kestrel = kestrel }), token);

            WebApplicationBuilder builder = LightClientHost.CreateBuilder([
                "--environment", Environments.Production, "--contentRoot", directory,
                "--urls", configuredEndpoint ? "http://127.0.0.1:0" : "https://127.0.0.1:0",
            ]);
            builder.Services.AddSingleton<IConfigProvider>(new ConfigProvider(new JsonRpcConfig { AllowedHosts = ["*"] }));
            builder.Services.AddSingleton<IJsonRpcUrlCollection>(new JsonRpcUrlCollection(NullLogManager.Instance, new JsonRpcConfig(), includeWebSockets: false));
            builder.Services.AddSingleton<ApplicationLifetime>();
            builder.Services.Replace(ServiceDescriptor.Singleton<IHostApplicationLifetime>(services => services.GetRequiredService<ApplicationLifetime>()));
            builder.WebHost.UseUrls(LightClientHost.ReadRpcUrls(builder.Configuration));
            WebApplication application = builder.Build();
            await using SharedWebHost host = new(application.Services, application.Configuration, new Startup(), NullLogManager.Instance);
            await host.StartAsync(token);
            Uri address = new(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Assert.That(address.Scheme, Is.EqualTo(Uri.UriSchemeHttps));
            using HttpClientHandler handler = new()
            {
                ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint,
            };
            using HttpClient client = new(handler);

            string response = await client.GetStringAsync(address, token);

            Assert.That(response, Is.EqualTo("configured HTTPS"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Startup : IStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services) => throw new NotSupportedException();

        public void Configure(IApplicationBuilder app) =>
            app.Run(context => context.Response.WriteAsync("configured HTTPS", context.RequestAborted));
    }
}
