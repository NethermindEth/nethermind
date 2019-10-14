/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.Runner.Config;
using Nethermind.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Runner
{
    public class Startup
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<KestrelServerOptions>(options => { options.AllowSynchronousIO = true; });
            Bootstrap.Instance.RegisterJsonRpcServices(services);
            var corsOrigins = Environment.GetEnvironmentVariable("NETHERMIND_CORS_ORIGINS") ?? "*";
            services.AddCors(c => c.AddPolicy("Cors",
                p => p.AllowAnyMethod().AllowAnyHeader().WithOrigins(corsOrigins)));
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcService jsonRpcService)
        {
            foreach (var converter in jsonRpcService.Converters)
            {
                JsonSettings.Converters.Add(converter);
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("Cors");
            
            var initConfig = app.ApplicationServices.GetService<IConfigProvider>().GetConfig<IInitConfig>();
            if (initConfig.WebSocketsEnabled)
            {
                app.UseWebSockets();
                app.UseWhen(ctx =>
                    ctx.WebSockets.IsWebSocketRequest && ctx.Request.Path.HasValue &&
                    ctx.Request.Path.Value.StartsWith("/ws"), builder => builder.UseWebSocketsModules());
            }

            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method == "GET")
                {
                    await ctx.Response.WriteAsync("Nethermind JSON RPC");
                    return;
                }

                if (ctx.Request.Method != "POST")
                {
                    return;
                }

                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var request = await reader.ReadToEndAsync();
                var result = await jsonRpcProcessor.ProcessAsync(request);
                await ctx.Response.WriteAsync(result.IsCollection
                    ? JsonConvert.SerializeObject(result.Responses, JsonSettings)
                    : JsonConvert.SerializeObject(result.Responses[0], JsonSettings));
            });
        }
    }
}
