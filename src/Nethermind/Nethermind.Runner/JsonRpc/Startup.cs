// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
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
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Runner.JsonRpc
{
    public class Startup
    {
        private static ReadOnlySpan<byte> _jsonOpeningBracket => new byte[] { (byte)'[' };
        private static ReadOnlySpan<byte> _jsonComma => new byte[] { (byte)',' };
        private static ReadOnlySpan<byte> _jsonClosingBracket => new byte[] { (byte)']' };

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
                options.Limits.MaxRequestBodySize = jsonRpcConfig.MaxRequestBodySize;
                options.ConfigureHttpsDefaults(co => co.SslProtocols |= SslProtocols.Tls13);
            });
            Bootstrap.Instance.RegisterJsonRpcServices(services);

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
            void SerializeTimeoutException(IJsonRpcService service, IBufferWriter<byte> resultStream)
            {
                JsonRpcErrorResponse? error = service.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                jsonSerializer.Serialize(resultStream, error);
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
                    if (jsonRpcUrl.IsAuthenticated && !rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
                    {
                        JsonRpcErrorResponse? response = jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Authentication error");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        jsonSerializer.Serialize(ctx.Response.BodyWriter, response);
                        await ctx.Response.CompleteAsync();
                        return;
                    }

                    Stopwatch stopwatch = Stopwatch.StartNew();
                    CountingPipeReader request = new(ctx.Request.BodyReader);
                    try
                    {
                        JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
                        await foreach (JsonRpcResult result in jsonRpcProcessor.ProcessAsync(request, jsonRpcContext))
                        {
                            using Stream stream = jsonRpcConfig.BufferResponses ? RecyclableStream.GetStream("http") : null;
                            ICountingBufferWriter resultWriter = stream is not null ? new CountingStreamPipeWriter(stream) : new CountingPipeWriter(ctx.Response.BodyWriter);
                            try
                            {
                                ctx.Response.ContentType = "application/json";
                                ctx.Response.StatusCode = GetStatusCode(result);

                                if (result.IsCollection)
                                {
                                    resultWriter.Write(_jsonOpeningBracket);
                                    bool first = true;
                                    JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses.GetAsyncEnumerator(CancellationToken.None);
                                    try
                                    {
                                        while (await enumerator.MoveNextAsync())
                                        {
                                            JsonRpcResult.Entry entry = enumerator.Current;
                                            using (entry)
                                            {
                                                if (!first)
                                                {
                                                    resultWriter.Write(_jsonComma);
                                                }

                                                first = false;
                                                jsonSerializer.Serialize(resultWriter, entry.Response);
                                                _ = jsonRpcLocalStats.ReportCall(entry.Report);

                                                // We reached the limit and don't want to responded to more request in the batch
                                                if (!jsonRpcContext.IsAuthenticated && resultWriter.WrittenCount > jsonRpcConfig.MaxBatchResponseBodySize)
                                                {
                                                    if (logger.IsWarn) logger.Warn($"The max batch response body size exceeded. The current response size {resultWriter.WrittenCount}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
                                                    enumerator.IsStopped = true;
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        await enumerator.DisposeAsync();
                                    }

                                    resultWriter.Write(_jsonClosingBracket);
                                }
                                else
                                {
                                    using (result.Response)
                                    {
                                        jsonSerializer.Serialize(resultWriter, result.Response);
                                    }
                                }

                                if (stream is not null)
                                {
                                    ctx.Response.ContentLength = resultWriter.WrittenCount;
                                    stream.Seek(0, SeekOrigin.Begin);
                                    await stream.CopyToAsync(ctx.Response.Body);
                                }
                            }
                            catch (Exception e) when (e.InnerException is OperationCanceledException)
                            {
                                SerializeTimeoutException(jsonRpcService, resultWriter);
                            }
                            catch (OperationCanceledException)
                            {
                                SerializeTimeoutException(jsonRpcService, resultWriter);
                            }
                            finally
                            {
                                await ctx.Response.CompleteAsync();
                            }

                            long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                            _ = jsonRpcLocalStats.ReportCall(result.IsCollection
                                ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                                : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);

                            Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);

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
                return IsResourceUnavailableError(result.Response)
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
            }
        }

        private static bool IsResourceUnavailableError(JsonRpcResponse? response)
        {
            return response is JsonRpcErrorResponse { Error.Code: ErrorCodes.ModuleTimeout }
                        or JsonRpcErrorResponse { Error.Code: ErrorCodes.LimitExceeded };
        }

        private sealed class CountingPipeReader : PipeReader
        {
            private readonly PipeReader _wrappedReader;
            private ReadOnlySequence<byte> _currentSequence;

            public long Length { get; private set; }

            public CountingPipeReader(PipeReader stream)
            {
                _wrappedReader = stream;
            }

            public override void AdvanceTo(SequencePosition consumed)
            {
                Length += _currentSequence.GetOffset(consumed);
                _wrappedReader.AdvanceTo(consumed);
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
                Length += _currentSequence.GetOffset(consumed);
                _wrappedReader.AdvanceTo(consumed, examined);
            }

            public override void CancelPendingRead()
            {
                _wrappedReader.CancelPendingRead();
            }

            public override void Complete(Exception? exception = null)
            {
                Length += _currentSequence.Length;
                _wrappedReader.Complete(exception);
            }

            public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                ReadResult result = await _wrappedReader.ReadAsync(cancellationToken);
                _currentSequence = result.Buffer;
                return result;
            }

            public override bool TryRead(out ReadResult result)
            {
                bool didRead = _wrappedReader.TryRead(out result);
                if (didRead)
                {
                    _currentSequence = result.Buffer;
                }

                return didRead;
            }
        }
    }
}
