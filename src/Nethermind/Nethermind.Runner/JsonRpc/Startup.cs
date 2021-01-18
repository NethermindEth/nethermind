﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;
using Newtonsoft.Json;
using HealthChecks.UI.Client;
using Nethermind.HealthChecks;
using Nethermind.Logging;

namespace Nethermind.Runner
{
    public class Startup
    {
        private IJsonSerializer _jsonSerializer = CreateJsonSerializer();
        
        private static EthereumJsonSerializer CreateJsonSerializer() => new EthereumJsonSerializer();

        public void ConfigureServices(IServiceCollection services)
        {
            var sp = services.BuildServiceProvider();
            IConfigProvider configProvider = sp.GetService<IConfigProvider>();
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();

            services.Configure<KestrelServerOptions>(options => {
                options.AllowSynchronousIO = true;
                options.Limits.MaxRequestBodySize = jsonRpcConfig.MaxRequestBodySize;
                options.ConfigureHttpsDefaults(co => co.SslProtocols |= SslProtocols.Tls13);
            });
            Bootstrap.Instance.RegisterJsonRpcServices(services);
            services.AddControllers();
            string corsOrigins = Environment.GetEnvironmentVariable("NETHERMIND_CORS_ORIGINS") ?? "*";
            services.AddCors(c => c.AddPolicy("Cors",
                p => p.AllowAnyMethod().AllowAnyHeader().WithOrigins(corsOrigins)));
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats)
        {
            void SerializeTimeoutException(IJsonRpcService service, Stream resultStream)
            {
                JsonRpcErrorResponse? error = service.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                _jsonSerializer.Serialize(resultStream, error);
            }

            _jsonSerializer = CreateJsonSerializer();
            
            foreach (JsonConverter converter in jsonRpcService.Converters)
            {
                _jsonSerializer.RegisterConverter(converter);
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("Cors");
            app.UseRouting();

            IConfigProvider configProvider = app.ApplicationServices.GetService<IConfigProvider>();
            ILogManager logManager = app.ApplicationServices.GetService<ILogManager>();
            ILogger logger = logManager.GetClassLogger();
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
            IHealthChecksConfig healthChecksConfig = configProvider.GetConfig<IHealthChecksConfig>();
            if (initConfig.WebSocketsEnabled)
            {
                WebSocketOptions opt = new WebSocketOptions();
                app.UseWebSockets(new WebSocketOptions());
                app.UseWhen(ctx => ctx.WebSockets.IsWebSocketRequest 
                                   && ctx.Connection.LocalPort == jsonRpcConfig.WebSocketsPort,
                builder => builder.UseWebSocketsModules());
            }
            
            app.UseEndpoints(endpoints =>
            {
                if (healthChecksConfig.Enabled)
                {
                    try
                    {
                        endpoints.MapHealthChecks(healthChecksConfig.Slug, new HealthCheckOptions()
                        {
                            Predicate = _ => true,
                            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                        });
                        if (healthChecksConfig.UIEnabled)
                        {
                            endpoints.MapHealthChecksUI(setup => setup.AddCustomStylesheet(Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "nethermind.css")));
                        }
                    }
                    catch (Exception e)
                    {
                        if (logger.IsError) logger.Error("Unable to initialize health checks. Check if you have Nethermind.HealthChecks.dll in your plugins folder.", e);
                    }
                }
            });

            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method == "GET")
                {
                    await ctx.Response.WriteAsync("Nethermind JSON RPC");
                }
                if (ctx.Connection.LocalPort == jsonRpcConfig.Port && ctx.Request.Method == "POST")
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    using StreamReader reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    string request = await reader.ReadToEndAsync();
                    using JsonRpcResult result = await jsonRpcProcessor.ProcessAsync(request);

                    ctx.Response.ContentType = "application/json";

                    Stream resultStream = jsonRpcConfig.BufferResponses ? new MemoryStream() : ctx.Response.Body;

                    try
                    {
                        if (result.IsCollection)
                        {
                            _jsonSerializer.Serialize(resultStream, result.Responses);
                        }
                        else
                        {
                            _jsonSerializer.Serialize(resultStream, result.Response);
                        }

                        if (jsonRpcConfig.BufferResponses)
                        {
                            ctx.Response.ContentLength = resultStream.Length;
                            resultStream.Seek(0, SeekOrigin.Begin);
                            await resultStream.CopyToAsync(ctx.Response.Body);
                        }
                    }
                    catch (Exception e) when (e.InnerException is OperationCanceledException)
                    {
                        SerializeTimeoutException(jsonRpcService, resultStream);
                    }
                    catch (OperationCanceledException)
                    {
                        SerializeTimeoutException(jsonRpcService, resultStream);
                    }
                    finally
                    {
                        await ctx.Response.CompleteAsync();
                        
                        if (jsonRpcConfig.BufferResponses)
                        {
                            await resultStream.DisposeAsync();
                        }
                    }
                    
                    if (result.IsCollection)
                    {
                        jsonRpcLocalStats.ReportCalls(result.Reports);
                        jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", stopwatch.ElapsedMicroseconds(), true));
                    }
                    else
                    {
                        jsonRpcLocalStats.ReportCall(result.Report, stopwatch.ElapsedMicroseconds());
                    }
                }
            });
        }
    }
}
