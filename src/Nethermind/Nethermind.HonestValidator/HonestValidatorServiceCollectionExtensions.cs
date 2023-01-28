// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Services;

namespace Nethermind.HonestValidator
{
    public static class HonestValidatorServiceCollectionExtensions
    {
        public static void AddHonestValidator(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<BeaconChainInformation>();
            services.AddSingleton<ValidatorClient>();

            services.AddHostedService<HonestValidatorWorker>();
        }
    }
}
