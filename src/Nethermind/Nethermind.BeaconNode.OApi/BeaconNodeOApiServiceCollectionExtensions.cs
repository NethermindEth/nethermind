// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Core2.Json;

namespace Nethermind.BeaconNode.OApi
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNodeOApi(this IServiceCollection services, IWebHostEnvironment env)
        {
            // Register controllers
            var apiAssembly = typeof(BeaconNodeServiceCollectionExtensions).GetTypeInfo().Assembly;
            services.AddControllers()
                .AddMvcOptions(mvcOptions =>
                {
                    mvcOptions.ModelBinderProviders.Insert(0, new PrefixedHexByteArrayModelBinderProvider());
                })
                .AddJsonOptions(jsonOptions =>
                {
                    if (env.IsDevelopment())
                    {
                        jsonOptions.JsonSerializerOptions.WriteIndented = true;
                    }

                    jsonOptions.JsonSerializerOptions.ConfigureNethermindCore2();
                })
                .AddApplicationPart(apiAssembly);
        }
    }
}
