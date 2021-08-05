//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using Newtonsoft.Json;

namespace Nethermind.Runner.JsonRpc
{
    public class Startup
    {
        private IJsonSerializer _jsonSerializer = CreateJsonSerializer();
        
        private static EthereumJsonSerializer CreateJsonSerializer() => new();

        public void ConfigureServices(IServiceCollection services)
        {
            // ReSharper disable once ASP0000
            ServiceProvider sp = services.BuildServiceProvider()!;
            IConfigProvider? configProvider = sp.GetService<IConfigProvider>();
            if (configProvider == null)
            {
                throw new ApplicationException($"{nameof(IConfigProvider)} could not be resolved");
            }
            
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
            
            services.AddResponseCompression(options =>
            {
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
                options.EnableForHttps = true;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats)
        {
            long SerializeTimeoutException(IJsonRpcService service, Stream resultStream)
            {
                JsonRpcErrorResponse? error = service.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                return _jsonSerializer.Serialize(resultStream, error);
            }

            _jsonSerializer = CreateJsonSerializer();
            _jsonSerializer.RegisterConverters(jsonRpcService.Converters);
            
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("Cors");
            app.UseRouting();
            app.UseResponseCompression();

            IConfigProvider? configProvider = app.ApplicationServices.GetService<IConfigProvider>();
            if (configProvider == null)
            {
                throw new ApplicationException($"{nameof(IConfigProvider)} has not been loaded properly");
            }
            
            ILogManager? logManager = app.ApplicationServices.GetService<ILogManager>() ?? NullLogManager.Instance;
            ILogger logger = logManager.GetClassLogger();
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
            IHealthChecksConfig healthChecksConfig = configProvider.GetConfig<IHealthChecksConfig>();
            if (initConfig.WebSocketsEnabled)
            {
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
                    string request;
                    try
                    {                    
                        using StreamReader reader = new(ctx.Request.Body, Encoding.UTF8);
                        request = await reader.ReadToEndAsync();
                    }
                    catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
                    {
                        if (logger.IsDebug) logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                        return;
                    }

                    Interlocked.Add(ref Nethermind.JsonRpc.Metrics.JsonRpcBytesReceivedHttp, ctx.Request.ContentLength ?? request.Length);
                    using JsonRpcResult result = await jsonRpcProcessor.ProcessAsync(request, JsonRpcContext.Http);

                    Stream resultStream = jsonRpcConfig.BufferResponses ? new MemoryStream() : ctx.Response.Body;

                    long responseSize = 0;
                    try
                    {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = GetStatusCode(result);
                        
                        responseSize = result.IsCollection 
                            ? _jsonSerializer.Serialize(resultStream, result.Responses) 
                            : _jsonSerializer.Serialize(resultStream, result.Response);

                        if (jsonRpcConfig.BufferResponses)
                        {
                            ctx.Response.ContentLength = responseSize = resultStream.Length;
                            resultStream.Seek(0, SeekOrigin.Begin);
                            await resultStream.CopyToAsync(ctx.Response.Body);
                        }
                    }
                    catch (Exception e) when (e.InnerException is OperationCanceledException)
                    {
                        responseSize = SerializeTimeoutException(jsonRpcService, resultStream);
                    }
                    catch (OperationCanceledException)
                    {
                        responseSize = SerializeTimeoutException(jsonRpcService, resultStream);
                    }
                    finally
                    {
                        await ctx.Response.CompleteAsync();

                        if (jsonRpcConfig.BufferResponses)
                        {
                            await resultStream.DisposeAsync();
                        }
                    }

                    long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                    if (result.IsCollection)
                    {
                        jsonRpcLocalStats.ReportCalls(result.Reports);
                        jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, responseSize);
                    }
                    else
                    {
                        jsonRpcLocalStats.ReportCall(result.Report, handlingTimeMicroseconds, responseSize);
                    }
                    
                    Interlocked.Add(ref Nethermind.JsonRpc.Metrics.JsonRpcBytesSentHttp, responseSize);
                }
            });
        }

        private static int GetStatusCode(JsonRpcResult result) =>
            ModuleTimeout(result) 
                ? StatusCodes.Status503ServiceUnavailable 
                : StatusCodes.Status200OK;

        private static bool ModuleTimeout(JsonRpcResult result)
        {
            static bool ModuleTimeoutError(JsonRpcResponse response) => 
                response is JsonRpcErrorResponse errorResponse && errorResponse.Error?.Code == ErrorCodes.ModuleTimeout;

            if (result.IsCollection)
            {
                for (var i = 0; i < result.Responses.Count; i++)
                {
                    if (ModuleTimeoutError(result.Responses[i]))
                    {
                        return true;
                    }
                }
            }
            else if (ModuleTimeoutError(result.Response))
            {
                return true;
            }

            return false;
        }
    }
}
