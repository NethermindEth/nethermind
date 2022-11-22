// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.OApiClient.Configuration;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Polly;
using Polly.Extensions.Http;

namespace Nethermind.BeaconNode.OApiClient
{
    public static class BeaconNodeOApiClientServiceCollectionExtensions
    {
        public static void AddBeaconNodeOapiClient(this IServiceCollection services, IConfiguration configuration)
        {
            AddConfiguration(services, configuration);

            services.AddSingleton<IBeaconNodeApi, BeaconNodeProxy>();
            services.AddHttpClient<IBeaconNodeApi, BeaconNodeProxy>(client =>
                {
                    var remoteUrls = configuration.GetSection("BeaconNodeConnection:RemoteUrls").Get<string[]>();
                    client.BaseAddress = new Uri(remoteUrls.First());
                    client.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
                })
                .AddPolicyHandler((services, request) => GetBeaconNodeProxyRetryPolicy(services, request));
        }

        private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<BeaconNodeConnection>(x =>
            {
                configuration.Bind("BeaconNodeConnection", section =>
                {
                    x.RemoteUrls = section.GetSection(nameof(x.RemoteUrls)).Get<string[]>();
                    x.ConnectionFailureLoopMillisecondsDelay =
                        section.GetValue<int>("ConnectionFailureLoopMillisecondsDelay", 1000);
                });
            });
        }

        private static IAsyncPolicy<HttpResponseMessage> GetBeaconNodeProxyRetryPolicy(IServiceProvider services,
            HttpRequestMessage request)
        {
            // TODO: Add rotate through connections policy
            // TODO: Add API Key for each connection

            ILogger<BeaconNodeProxy> logger = services.GetService<ILogger<BeaconNodeProxy>>();
            IOptions<BeaconNodeConnection> beaconNodeConnectionOptions =
                services.GetService<IOptions<BeaconNodeConnection>>();

            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.NotFound)
                .WaitAndRetryForeverAsync(
                    (retryAttempt, context) =>
                        TimeSpan.FromMilliseconds(beaconNodeConnectionOptions.Value
                            .ConnectionFailureLoopMillisecondsDelay),
                    (outcome, retryAttempt, timespan, context) =>
                    {
                        Log.NodeConnectionRetry(logger, outcome.Result?.RequestMessage.RequestUri,
                            outcome.Result?.StatusCode, outcome.Result?.ReasonPhrase, timespan, retryAttempt,
                            outcome.Exception);
                    });
        }
    }
}
