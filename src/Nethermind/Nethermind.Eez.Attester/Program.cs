// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    internal static WebApplication Build(WebApplicationBuilder builder, AttesterOptions options, AttestationPipeline pipeline)
    {
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.RequestTimeout + TimeSpan.FromMinutes(1));
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = options.Limits.MaxMessageBytes;
            grpc.MaxSendMessageSize = 1024;
        });
        builder.Services.AddSingleton(pipeline);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ProverService>();

        WebApplication app = builder.Build();
        app.MapGrpcService<ProverService>();
        ProverService prover = app.Services.GetRequiredService<ProverService>();
        app.Lifetime.ApplicationStopping.Register(() => prover.WaitUntilIdle().GetAwaiter().GetResult());
        return app;
    }

    private static async Task Serve(AttesterOptions options)
    {
        TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder());
        ILogManager logManager = SimpleConsoleLogManager.Instance;
        PrivateKey key = AttestationKeyStore.Unlock(options.KeyStoreDirectory, options.PasswordFile, options.AttesterAddress, logManager);
        ISpecProvider specProvider = ChainConfigLoader.Load(options.ChainConfigPath);
        EezSettlementContext context = new(options.RollupId, specProvider.ChainId, options.ProofSystem, options.VerificationKey, options.BlockTimeSeconds, options.GasLimit);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.ListenAddress, listen => listen.Protocols = HttpProtocols.Http2));
        WebApplication app = Build(builder, options, new AttestationPipeline(specProvider, context, new EezAttestationSigner(key), logManager));

        ILogger<ProverService> logger = app.Services.GetRequiredService<ILogger<ProverService>>();
        if (!options.ListenAddress.Address.Equals(System.Net.IPAddress.Loopback))
        {
            logger.LogWarning("Prove service listens on non-loopback address {Address}", options.ListenAddress);
        }

        logger.LogInformation(
            "serving Prove on {Address}: rollup {RollupId}, L2 chain {ChainId}, attester {Attester}, proof system {ProofSystem}, block time {BlockTime} s, limits {Limits}",
            options.ListenAddress, options.RollupId, specProvider.ChainId, key.Address, options.ProofSystem, options.BlockTimeSeconds, options.Limits);
        await app.RunAsync();
    }
}
