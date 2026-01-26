// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Primitives;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Resettables;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using System.Reflection;

namespace Nethermind.Runner.JsonRpc;

public class Startup : IStartup
{
    private static ReadOnlySpan<byte> _jsonOpeningBracket => [(byte)'['];
    private static ReadOnlySpan<byte> _jsonComma => [(byte)','];
    private static ReadOnlySpan<byte> _jsonClosingBracket => [(byte)']'];

    IServiceProvider IStartup.ConfigureServices(IServiceCollection services) => Build(services);

    public void ConfigureServices(IServiceCollection services)
    {
        ServiceProvider sp = Build(services);
        IConfigProvider? configProvider = sp.GetService<IConfigProvider>() ?? throw new ApplicationException($"{nameof(IConfigProvider)} could not be resolved");
        IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = jsonRpcConfig.MaxRequestBodySize;
            options.ConfigureHttpsDefaults(co => co.SslProtocols |= SslProtocols.Tls13);
            options.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
                listenOptions.DisableAltSvcHeader = true;
            });
        });
        Bootstrap.Instance.RegisterJsonRpcServices(services);

        services.AddCors(options => options.AddDefaultPolicy(builder => builder
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithOrigins(jsonRpcConfig.CorsOrigins)));

        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
            options.EnableForHttps = true;
        });
    }

    private static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider();


    public void Configure(IApplicationBuilder app)
    {
        IServiceProvider services = app.ApplicationServices;
        Configure(
            app,
            services.GetRequiredService<IWebHostEnvironment>(),
            services.GetRequiredService<IJsonRpcProcessor>(),
            services.GetRequiredService<IJsonRpcService>(),
            services.GetRequiredService<IJsonRpcLocalStats>(),
            services.GetRequiredService<IJsonSerializer>(),
            services.GetRequiredService<ApplicationLifetime>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats, IJsonSerializer jsonSerializer, ApplicationLifetime lifetime)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseRouting();
        app.UseCors();

        IConfigProvider? configProvider = app.ApplicationServices.GetService<IConfigProvider>();
        IRpcAuthentication? rpcAuthentication = app.ApplicationServices.GetService<IRpcAuthentication>();
        IRpcModuleProvider? rpcModuleProvider = ResolveRpcModuleProvider(jsonRpcService, app.ApplicationServices);

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
        IReadOnlyDictionary<string, RpcModuleProvider.ResolvedMethodInfo>? restMethodsByPath = (rpcModuleProvider as RpcModuleProvider)?.GetRestMethodsByPath();

        // If request is local, don't use response compression,
        // as it allocates a lot, but doesn't improve much for loopback
        app.UseWhen(ctx =>
            !IsLocalhost(ctx.Connection.RemoteIpAddress),
            builder => builder.UseResponseCompression());

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

                IServiceProvider services = app.ApplicationServices;
                endpoints.MapDataFeeds(lifetime);
            }
        });

        if (restMethodsByPath is { Count: > 0 })
        {
            app.MapWhen(
                ctx => HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.HasValue && restMethodsByPath.ContainsKey(ctx.Request.Path.Value!),
                builder => builder.Run(async ctx =>
                {
                    if (jsonRpcProcessor.ProcessExit.IsCancellationRequested)
                    {
                        ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        return;
                    }

                    if (!jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
                    {
                        ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (jsonRpcUrl.IsAuthenticated)
                    {
                        if (!await rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
                    }

                    if (IsJsonRequested(ctx.Request.Headers.Accept))
                    {
                        ctx.Response.Headers.Accept = "application/octet-stream";
                        ctx.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                        ctx.Response.ContentType = "text/plain";
                        await ctx.Response.WriteAsync("JSON responses are not supported for this endpoint. Use application/octet-stream.");
                        await ctx.Response.CompleteAsync();
                        return;
                    }

                    string path = ctx.Request.Path.Value!;
                    RpcModuleProvider.ResolvedMethodInfo methodInfo = restMethodsByPath[path];

                    if (!TryBuildRestParams(ctx.Request, methodInfo.MethodInfo, out JsonElement parameters, out string? errorMessage))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                        ctx.Response.ContentType = "text/plain";
                        await ctx.Response.WriteAsync(errorMessage ?? "Invalid parameters.");
                        await ctx.Response.CompleteAsync();
                        return;
                    }

                    using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
                    JsonRpcRequest request = new()
                    {
                        Method = methodInfo.MethodInfo.Name,
                        Params = parameters
                    };

                    JsonRpcResponse response = await jsonRpcService.SendRequestAsync(request, jsonRpcContext);
                    using (response)
                    {
                        if (response is not JsonRpcSuccessResponse success)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            await ctx.Response.CompleteAsync();
                            return;
                        }

                        if (success.Result is null)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                            await ctx.Response.CompleteAsync();
                        }

                        Type? declaredResultType = GetDeclaredResultType(methodInfo.MethodInfo);
                        if (!RestSszEncoder.TryEncode(success.Result, declaredResultType, out byte[]? encoded))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            await ctx.Response.CompleteAsync();
                            return;
                        }

                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength = encoded?.Length ?? 0;
                        if (encoded is { Length: > 0 })
                        {
                            await ctx.Response.Body.WriteAsync(encoded);
                        }

                        await ctx.Response.CompleteAsync();
                    }
                }));
        }

        app.MapWhen(
            (ctx) => ctx.Request.ContentType?.Contains("application/json") ?? false,
            builder => builder.Run(async ctx =>
        {
            var method = ctx.Request.Method;
            if (method is not "POST" and not "GET")
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (jsonRpcProcessor.ProcessExit.IsCancellationRequested)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            if (!jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (jsonRpcUrl.IsAuthenticated)
            {
                if (!await rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
                {
                    await PushErrorResponse(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRequest, "Authentication error");
                    return;
                }
            }

            if (method == "GET" && ctx.Request.Headers.Accept.Count > 0 &&
                !ctx.Request.Headers.Accept[0].Contains("text/html", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("Nethermind JSON RPC");
            }
            else if (ctx.Request.ContentType?.Contains("application/json") == false)
            {
                await PushErrorResponse(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.InvalidRequest, "Missing 'application/json' Content-Type header");
            }
            else
            {
                if (jsonRpcUrl.MaxRequestBodySize is not null)
                    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;

                long startTime = Stopwatch.GetTimestamp();
                CountingPipeReader request = new(ctx.Request.BodyReader);
                try
                {
                    using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
                    await foreach (JsonRpcResult result in jsonRpcProcessor.ProcessAsync(request, jsonRpcContext))
                    {
                        using (result)
                        {
                            await using Stream stream = jsonRpcConfig.BufferResponses ? RecyclableStream.GetStream("http") : null;
                            CountingWriter resultWriter = stream is not null ? new CountingStreamPipeWriter(stream) : new CountingPipeWriter(ctx.Response.BodyWriter);
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
                                                await jsonSerializer.SerializeAsync(resultWriter, entry.Response);
                                                _ = jsonRpcLocalStats.ReportCall(entry.Report);

                                                // We reached the limit and don't want to responded to more request in the batch
                                                if (!jsonRpcContext.IsAuthenticated && resultWriter.WrittenCount > jsonRpcConfig.MaxBatchResponseBodySize)
                                                {
                                                    if (logger.IsWarn)
                                                        logger.Warn(
                                                            $"The max batch response body size exceeded. The current response size {resultWriter.WrittenCount}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
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
                                    await jsonSerializer.SerializeAsync(resultWriter, result.Response);
                                }
                                await resultWriter.CompleteAsync();
                                if (stream is not null)
                                {
                                    ctx.Response.ContentLength = resultWriter.WrittenCount;
                                    stream.Seek(0, SeekOrigin.Begin);
                                    await stream.CopyToAsync(ctx.Response.Body);
                                }
                            }
                            catch (Exception e) when (e.InnerException is OperationCanceledException)
                            {
                                await SerializeTimeoutException(resultWriter);
                            }
                            catch (OperationCanceledException)
                            {
                                await SerializeTimeoutException(resultWriter);
                            }
                            finally
                            {
                                await ctx.Response.CompleteAsync();
                            }

                            long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                            _ = jsonRpcLocalStats.ReportCall(result.IsCollection
                                ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                                : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);

                            Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);

                            // There should be only one response because we don't expect multiple JSON tokens in the request
                            break;
                        }
                    }
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
                {
                    if (logger.IsDebug) logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                    await PushErrorResponse(e.StatusCode, e.StatusCode == StatusCodes.Status413PayloadTooLarge
                                            ? ErrorCodes.LimitExceeded
                                            : ErrorCodes.InvalidRequest,
                                            e.Message);
                }
                finally
                {
                    Interlocked.Add(ref Nethermind.JsonRpc.Metrics.JsonRpcBytesReceivedHttp, ctx.Request.ContentLength ?? request.Length);
                }
            }
            Task SerializeTimeoutException(CountingWriter resultStream)
            {
                JsonRpcErrorResponse? error = jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                return jsonSerializer.SerializeAsync(resultStream, error);
            }
            async Task PushErrorResponse(int statusCode, int errorCode, string message)
            {
                JsonRpcErrorResponse? response = jsonRpcService.GetErrorResponse(errorCode, message);
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = statusCode;
                await jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, response);
                await ctx.Response.CompleteAsync();
            }
        }));

        if (healthChecksConfig.Enabled)
        {
            var fileProvider = new ManifestEmbeddedFileProvider(typeof(Startup).Assembly, "wwwroot");

            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }
    }

    /// <summary>
    /// Check for IPv4 localhost (127.0.0.1) and IPv6 localhost (::1)
    /// </summary>
    /// <param name="remoteIp">Request source</param>
    private static bool IsLocalhost(IPAddress remoteIp)
        => IPAddress.IsLoopback(remoteIp) || remoteIp.Equals(IPAddress.IPv6Loopback);

    private static int GetStatusCode(in JsonRpcResult result)
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

    private static IRpcModuleProvider? ResolveRpcModuleProvider(IJsonRpcService jsonRpcService, IServiceProvider services)
    {
        IRpcModuleProvider? provider = services.GetService<IRpcModuleProvider>();
        if (provider is not null)
        {
            return provider;
        }

        FieldInfo? field = jsonRpcService.GetType().GetField("_rpcModuleProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(jsonRpcService) as IRpcModuleProvider;
    }

    private static bool IsJsonRequested(StringValues acceptHeaders)
    {
        if (acceptHeaders.Count == 0)
        {
            return false;
        }

        bool acceptsOctetStream = acceptHeaders.Any(value =>
            value.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("*/*", StringComparison.OrdinalIgnoreCase));

        bool requestsJson = acceptHeaders.Any(value =>
            value.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("text/json", StringComparison.OrdinalIgnoreCase));

        return requestsJson && !acceptsOctetStream;
    }

    private static bool TryBuildRestParams(HttpRequest request, MethodInfo methodInfo, out JsonElement parameters, out string? errorMessage)
    {
        ParameterInfo[] expectedParams = methodInfo.GetParameters();
        if (expectedParams.Length == 0)
        {
            parameters = JsonSerializer.SerializeToElement(Array.Empty<object?>());
            errorMessage = null;
            return true;
        }

        List<object?> args = new(expectedParams.Length);
        foreach (ParameterInfo param in expectedParams)
        {
            if (!TryGetQueryValue(request.Query, param.Name!, out StringValues values))
            {
                if (param.HasDefaultValue || param.IsOptional)
                {
                    break;
                }

                parameters = default;
                errorMessage = $"Missing required parameter '{param.Name}'.";
                return false;
            }

            args.Add(BuildRestArg(param.ParameterType, values));
        }

        parameters = JsonSerializer.SerializeToElement(args.ToArray());
        errorMessage = null;
        return true;
    }

    private static bool TryGetQueryValue(IQueryCollection query, string name, out StringValues values)
    {
        if (TryGetQueryValueInternal(query, name, out values) || TryGetQueryValueInternal(query, ToSnakeCase(name), out values))
        {
            values = ExpandQueryValues(values);
            return true;
        }

        values = default;
        return false;
    }

    private static bool TryGetQueryValueInternal(IQueryCollection query, string name, out StringValues values)
    {
        if (query.TryGetValue(name, out values))
        {
            return true;
        }

        return query.TryGetValue($"{name}[]", out values);
    }

    private static StringValues ExpandQueryValues(StringValues values)
    {
        if (values.Count == 1)
        {
            string? value = values[0];
            if (string.IsNullOrWhiteSpace(value))
            {
                return values;
            }

            if (value[0] == '[' && value[^1] == ']')
            {
                try
                {
                    string[]? jsonValues = JsonSerializer.Deserialize<string[]>(value);
                    if (jsonValues is { Length: > 0 })
                    {
                        return new StringValues(jsonValues);
                    }
                }
                catch
                {
                    return values;
                }
            }

            if (value.Contains(',', StringComparison.Ordinal))
            {
                string[] split = value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (split.Length > 0)
                {
                    return new StringValues(split);
                }
            }
        }

        return values;
    }

    private static object? BuildRestArg(Type parameterType, StringValues values)
    {
        if (parameterType.IsArray || IsEnumerableParameter(parameterType))
        {
            return values.ToArray();
        }

        return values.Count == 0 ? null : values[0];
    }

    private static bool IsEnumerableParameter(Type parameterType)
    {
        if (parameterType == typeof(string) || parameterType == typeof(byte[]))
        {
            return false;
        }

        return parameterType.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        StringBuilder builder = new(input.Length + 4);
        builder.Append(char.ToLowerInvariant(input[0]));
        for (int i = 1; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static Type? GetDeclaredResultType(MethodInfo methodInfo)
    {
        Type returnType = methodInfo.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            returnType = returnType.GetGenericArguments()[0];
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ResultWrapper<>))
        {
            return returnType.GetGenericArguments()[0];
        }

        return null;
    }
}
