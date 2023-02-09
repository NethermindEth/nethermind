// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core2;

namespace Nethermind.Core2.Cryptography
{
    public static class CryptographyServiceCollectionExtensions
    {
        public static void AddCryptographyService(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<ICryptographyService, CryptographyService>();
        }
    }
}
