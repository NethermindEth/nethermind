// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RootCommand root = new("EEZ attester: re-executes settlement windows streamed by a composer and signs their public inputs hash");
        AttesterCommandLine.AddTo(root);
        root.SetAction(async (result, cancellation) => await Serve(AttesterCommandLine.Parse(result)));

        Command import = new("keystore-import", "Encrypts a raw private key read from standard input into the keystore");
        import.Options.Add(AttesterCommandLine.KeyStoreDirectory);
        import.Options.Add(AttesterCommandLine.PasswordFile);
        import.SetAction(result =>
        {
            Address address = AttestationKeyStore.Import(AttesterCommandLine.Required(result, AttesterCommandLine.KeyStoreDirectory),
                AttesterCommandLine.Required(result, AttesterCommandLine.PasswordFile), Console.In.ReadToEnd(), SimpleConsoleLogManager.Instance);
            Console.WriteLine(address);
        });
        root.Subcommands.Add(import);

        try
        {
            return await root.Parse(args).InvokeAsync();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>The Prove service on <paramref name="builder"/>'s server, draining the active request on shutdown.</summary>
    internal static WebApplication Build(WebApplicationBuilder builder, AttesterOptions options, AttestationPipeline pipeline, StreamingRuntime? streaming = null)
    {
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.RequestTimeout + TimeSpan.FromMinutes(1));
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = options.Limits.MaxMessageBytes;
            grpc.MaxSendMessageSize = 1024;
        });
        builder.Services.AddSingleton(pipeline);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<BackendAdmission>();
        builder.Services.AddSingleton(services => streaming is null
            ? new ProverService(pipeline, options, services.GetRequiredService<BackendAdmission>(), services.GetRequiredService<ILogger<ProverService>>())
            : streaming.CreateProver(pipeline, options, services.GetRequiredService<BackendAdmission>(), services.GetRequiredService<ILogger<ProverService>>()));
        if (streaming is not null)
        {
            streaming.ConfigureListeners(builder.WebHost, options);
            builder.Services.AddGrpc().AddServiceOptions<StreamingProverService>(grpc =>
                grpc.MaxReceiveMessageSize = streaming.Options.TransportLimits.MaxMessageBytes);
            builder.Services.AddSingleton(streaming.Identity);
            builder.Services.AddSingleton(streaming.Capabilities);
            builder.Services.AddSingleton(streaming.Registry);
            builder.Services.AddSingleton(services => streaming.CreateTransport(services.GetRequiredService<BackendAdmission>(),
                services.GetRequiredService<ILogger<StreamingTransport>>()));
            builder.Services.AddSingleton<StreamingProverService>();
        }

        WebApplication app = builder.Build();
        if (streaming is not null)
        {
            app.Use(async (context, next) =>
            {
                if (context.Features.Get<ITlsConnectionFeature>() is not null && context.Request.Path.StartsWithSegments("/prove.v1.Prover"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(context);
            });
            app.MapGrpcService<StreamingProverService>();
        }
        app.MapGrpcService<ProverService>();
        ProverService prover = app.Services.GetRequiredService<ProverService>();
        if (streaming is null) app.Lifetime.ApplicationStopping.Register(() => prover.WaitUntilIdle().GetAwaiter().GetResult());
        return app;
    }

    private static async Task Serve(AttesterOptions options)
    {
        ILogManager logManager = SimpleConsoleLogManager.Instance;
        PrivateKey key = AttestationKeyStore.Unlock(options.KeyStoreDirectory, options.PasswordFile, options.AttesterAddress, logManager);
        (ISpecProvider specProvider, EezChainRules rules) = ChainConfigLoader.LoadProfile(options.ChainConfigPath);
        if (options.SkipConsensusLayerCalls) rules = rules.WithoutConsensusLayerCalls();
        await InitializeExecution(rules, TxDecoder.Instance);
        if (rules.IsRollupX && options.StreamingConfigPath is null)
            throw new InvalidOperationException("RollupX attestation requires the native streaming profile");
        EezSettlementContext context = new(options.RollupId, specProvider.ChainId, options.ProofSystem, options.VerificationKey, options.BlockTimeSeconds, options.GasLimit);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        EezAttestationSigner signer = new(key);
        AttestationPipeline pipeline = new(specProvider, context, signer, logManager, rules);
        using StreamingRuntime? streaming = options.StreamingConfigPath is null ? null : StreamingRuntime.Load(options, specProvider, pipeline, signer, logManager, rules);
        if (streaming is null) builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.ListenAddress, listen => listen.Protocols = HttpProtocols.Http2));
        WebApplication app = Build(builder, options, pipeline, streaming);

        ILogger<ProverService> logger = app.Services.GetRequiredService<ILogger<ProverService>>();
        if (!options.ListenAddress.Address.Equals(System.Net.IPAddress.Loopback))
        {
            logger.LogWarning("Prove service listens on non-loopback address {Address}", options.ListenAddress);
        }

        logger.LogInformation(
            "serving Prove on {Address}: rollup {RollupId}, L2 chain {ChainId}, attester {Attester}, proof system {ProofSystem}, block time {BlockTime} s, limits {Limits}",
            options.ListenAddress, options.RollupId, specProvider.ChainId, key.Address, options.ProofSystem, options.BlockTimeSeconds, options.Limits);
        BackendAdmission admission = app.Services.GetRequiredService<BackendAdmission>();
        try { await app.StartAsync(); await app.WaitForShutdownAsync(); }
        finally { await StopAndDrain(app, admission); }
    }

    internal static async Task InitializeExecution(EezChainRules rules, TxDecoder<Transaction> decoder)
    {
        decoder.RegisterDecoder(EezTxType.CreateDecoder(rules));
        if (rules.UsesNativeContract) decoder.RegisterDecoder(new EezNativeHistoryTxDecoder(rules.NativeFormat));
        if (rules.IsRollupX) await KzgPolynomialCommitments.InitializeAsync();
    }

    internal static async Task StopAndDrain(WebApplication app, BackendAdmission admission)
    {
        ProverService? prover = null;
        try
        {
            try
            {
                prover = app.Services.GetService<ProverService>();
                await app.StopAsync();
            }
            finally { await app.DisposeAsync(); }
        }
        finally { await Task.WhenAll(admission.WaitUntilIdle(), prover?.WaitUntilIdle() ?? Task.CompletedTask); }
    }
}
