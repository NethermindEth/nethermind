// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using Newtonsoft.Json;

namespace Nethermind.Runner.JsonRpc
{
    public class Startup
    {
        private static readonly byte _jsonOpeningBracket = Convert.ToByte('{');
        private static readonly byte _jsonComma = Convert.ToByte(',');
        private static readonly byte _jsonClosingBracket = Convert.ToByte('}');

        public void ConfigureServices(IServiceCollection services)
        {
            ServiceProvider sp = Build(services);
            IConfigProvider? configProvider = sp.GetService<IConfigProvider>();
            if (configProvider is null)
            {
                throw new ApplicationException($"{nameof(IConfigProvider)} could not be resolved");
            }

            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();

            services.Configure<KestrelServerOptions>(options =>
            {
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

        private static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider();

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats, IJsonSerializer jsonSerializer)
        {
            long SerializeTimeoutException(IJsonRpcService service, Stream resultStream)
            {
                JsonRpcErrorResponse? error = service.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                return jsonSerializer.Serialize(resultStream, error);
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("Cors");
            app.UseRouting();
            app.UseResponseCompression();

            IConfigProvider? configProvider = app.ApplicationServices.GetService<IConfigProvider>();
            IRpcAuthentication? rpcAuthentication = app.ApplicationServices.GetService<IRpcAuthentication>();

            if (configProvider is null)
            {
                throw new ApplicationException($"{nameof(IConfigProvider)} has not been loaded properly");
            }

            ILogManager? logManager = app.ApplicationServices.GetService<ILogManager>() ?? NullLogManager.Instance;
            ILogger logger = logManager.GetClassLogger();
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
            IJsonRpcUrlCollection jsonRpcUrlCollection = app.ApplicationServices.GetRequiredService<IJsonRpcUrlCollection>();
            IHealthChecksConfig healthChecksConfig = configProvider.GetConfig<IHealthChecksConfig>();

            if (initConfig.WebSocketsEnabled)
            {
                app.UseWebSockets(new WebSocketOptions());
                app.UseWhen(ctx =>
                    ctx.WebSockets.IsWebSocketRequest &&
                    jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) &&
                    jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Ws),
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

            app.Run(async (ctx) =>
            {
                if (ctx.Request.Method == "GET")
                {
                    await ctx.Response.WriteAsync("Nethermind JSON RPC");
                }

                if (ctx.Request.Method == "POST" &&
                    jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) &&
                    jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
                {
                    if (jsonRpcUrl.IsAuthenticated && !rpcAuthentication!.Authenticate(ctx.Request.Headers["Authorization"]))
                    {
                        var response = jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Authentication error");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        jsonSerializer.Serialize(ctx.Response.Body, response);
                        await ctx.Response.CompleteAsync();
                        return;
                    }
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    using CountingTextReader request = new(new StreamReader(ctx.Request.Body, Encoding.UTF8));
                    try
                    {
                        await foreach (JsonRpcResult result in jsonRpcProcessor.ProcessAsync(request, JsonRpcContext.Http(jsonRpcUrl)))
                        {
                            Stream resultStream = jsonRpcConfig.BufferResponses ? new MemoryStream() : ctx.Response.Body;

                            long responseSize = 0;
                            try
                            {
                                ctx.Response.ContentType = "application/json";
                                ctx.Response.StatusCode = GetStatusCode(result);

                                if (result.IsCollection)
                                {
                                    resultStream.WriteByte(_jsonOpeningBracket);
                                    bool first = true;
                                    await foreach (JsonRpcResult.Entry entry in result.BatchedResponses)
                                    {
                                        using (entry)
                                        {
                                            if (!first) resultStream.WriteByte(_jsonComma);
                                            first = false;

                                            jsonSerializer.Serialize(resultStream, entry.Response);
                                            jsonRpcLocalStats.ReportCall(entry.Report);
                                        }
                                    }
                                    resultStream.WriteByte(_jsonClosingBracket);
                                }
                                else
                                {
                                    jsonSerializer.Serialize(resultStream, result.Response);
                                }

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
                            jsonRpcLocalStats.ReportCall(result.IsCollection
                                ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                                : result.Response.Value.Report, handlingTimeMicroseconds, responseSize);

                            Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, responseSize);

                            // There should be only one response because we don't expect multiple JSON tokens in the request
                            break;
                        }
                    }
                    catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
                    {
                        if (logger.IsDebug) logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                    }
                    finally
                    {
                        Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, ctx.Request.ContentLength ?? request.Length);
                    }
                }
            });
        }

        private static int GetStatusCode(JsonRpcResult result)
        {
            if (result.IsCollection)
            {
                return StatusCodes.Status200OK;
            }
            else
            {
                return ModuleTimeout(result.Response.Value)
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
            }
        }

        private static bool ModuleTimeout(JsonRpcResult.Entry result)
        {
            static bool ModuleTimeoutError(JsonRpcResponse response) =>
                response is JsonRpcErrorResponse errorResponse && errorResponse.Error?.Code == ErrorCodes.ModuleTimeout;

            if (ModuleTimeoutError(result.Response))
            {
                return true;
            }

            return false;
        }
    }
}
