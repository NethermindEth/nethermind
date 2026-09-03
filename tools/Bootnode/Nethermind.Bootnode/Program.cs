// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Autofac;
using Nethermind.Bootnode;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging.NLog;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using NLog;
using Prometheus;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NethermindLogger = Nethermind.Logging.ILogger;

bool isRunningInContainer = BootnodeOptionDefaults.IsRunningInContainer;

Option<string> dataDirOption = new("--data-dir")
{
    Description = "Directory for the bootnode key and discovered-node persistence.",
    DefaultValueFactory = _ => BootnodeOptionDefaults.DataDir(isRunningInContainer)
};

Option<int> discoveryPortOption = new("--discovery-port")
{
    Description = "UDP discovery port.",
    DefaultValueFactory = _ => 30303
};

Option<string?> addrOption = new("--addr")
{
    Description = "Bootnode-style UDP listen address, for example ':30301' or '127.0.0.1:30301'."
};

Option<int> httpPortOption = new("--http-port")
{
    Description = "HTTP REST/JSON-RPC port.",
    DefaultValueFactory = _ => 8546
};

Option<string> httpHostOption = new("--http-host")
{
    Description = "HTTP REST/JSON-RPC listen host.",
    DefaultValueFactory = _ => BootnodeOptionDefaults.ServiceHost(isRunningInContainer)
};

Option<int> metricsPortOption = new("--metrics-port")
{
    Description = "Prometheus metrics port.",
    DefaultValueFactory = _ => 6060
};

Option<string> metricsHostOption = new("--metrics-host")
{
    Description = "Prometheus metrics listen host.",
    DefaultValueFactory = _ => BootnodeOptionDefaults.ServiceHost(isRunningInContainer)
};

Option<string> protocolsOption = new("--protocols")
{
    Description = "Discovery protocols to enable: v4, v5, or all.",
    DefaultValueFactory = _ => "all"
};

Option<bool> activeDiscoveryOption = new("--active-discovery")
{
    Description = "Run continuous random Kademlia lookups in addition to table bootstrap and bucket refresh.",
    DefaultValueFactory = _ => true
};

Option<int> activeDiscoveryJobsOption = new("--active-discovery-jobs")
{
    Description = "Concurrent active discovery lookup jobs.",
    DefaultValueFactory = _ => 10
};

Option<int> bucketSizeOption = new("--bucket-size")
{
    Description = "Kademlia bucket size.",
    DefaultValueFactory = _ => 16
};

Option<int> concurrencyOption = new("--concurrency")
{
    Description = "Kademlia lookup concurrency.",
    DefaultValueFactory = _ => 3
};

Option<int> discoveryIntervalOption = new("--discovery-interval-ms")
{
    Description = "Interval between Kademlia bootstrap and bucket refresh passes, in milliseconds.",
    DefaultValueFactory = _ => 30000
};

Option<string?> localIpOption = new("--local-ip")
{
    Description = "Local IP address to bind the UDP discovery socket.",
    DefaultValueFactory = _ => BootnodeOptionDefaults.LocalIp(isRunningInContainer)
};

Option<string?> externalIpOption = new("--external-ip")
{
    Description = "Advertised external IP address."
};

Option<string?> externalIpV4Option = new("--external-ip-v4")
{
    Description = "External IPv4 address advertised when the listener enables IPv4. Use with --external-ip-v6 and --local-ip :: for a dual-stack ENR."
};

Option<string?> externalIpV6Option = new("--external-ip-v6")
{
    Description = "External IPv6 address advertised when the listener enables IPv6. Use --external-ip on IPv6-only hosts, or use with --external-ip-v4 and --local-ip :: for a dual-stack ENR."
};

Option<string[]> bootnodesOption = new("--bootnode", "--bootnodes")
{
    Description = "Bootnode enode/ENR values. May be repeated or comma-separated.",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => []
};

Option<bool> useDefaultDiscv5BootnodesOption = new("--use-default-discv5-bootnodes")
{
    Description = "Use Nethermind's embedded well-known discv5 bootnodes in addition to configured bootnodes.",
    DefaultValueFactory = _ => true
};

Option<string> logLevelOption = new("--log-level", "-l")
{
    Description = "Log level: Trace, Debug, Info, Warn, Error.",
    DefaultValueFactory = _ => "Info"
};

Option<string?> logFileOption = new("--log-file")
{
    Description = "Optional log file path."
};

Option<string?> privateKeyOption = new("--private-key", "--nodekeyhex")
{
    Description = "Hex-encoded secp256k1 node private key."
};

Option<string?> privateKeyFileOption = new("--private-key-file", "--nodekey")
{
    Description = "Path to a hex-encoded secp256k1 node private key file."
};

Option<bool> genKeyOption = new("--genkey")
{
    Description = "Generate the node key file and exit.",
    DefaultValueFactory = _ => false
};

Option<bool> writeAddressOption = new("--write-address")
{
    Description = "Print the local enode and ENR at startup.",
    DefaultValueFactory = _ => true
};

RootCommand rootCommand = new("Nethermind standalone discovery bootnode")
{
    dataDirOption,
    discoveryPortOption,
    addrOption,
    httpHostOption,
    httpPortOption,
    metricsHostOption,
    metricsPortOption,
    protocolsOption,
    activeDiscoveryOption,
    activeDiscoveryJobsOption,
    bucketSizeOption,
    concurrencyOption,
    discoveryIntervalOption,
    localIpOption,
    externalIpOption,
    externalIpV4Option,
    externalIpV6Option,
    bootnodesOption,
    useDefaultDiscv5BootnodesOption,
    logLevelOption,
    logFileOption,
    privateKeyOption,
    privateKeyFileOption,
    genKeyOption,
    writeAddressOption
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    BootnodeOptions options;
    try
    {
        options = CreateOptions(parseResult);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    Directory.CreateDirectory(options.DataDir);

    if (options.GenKey)
    {
        return GenerateKey(options);
    }

    LoggingConfigurator.Configure(options.LogLevel, options.LogFile);
    using NLogManager logManager = new();
    NethermindLogger logger = logManager.GetClassLogger<Program>();
    ConsoleCancelEventHandler? cancelHandler = null;

    try
    {
        using CancellationTokenSource shutdownSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!shutdownSource.IsCancellationRequested)
            {
                shutdownSource.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;

        using PrivateKey privateKey = LoadOrCreatePrivateKey(options, logger);
        IProtectedPrivateKey nodeKey = new ProtectedPrivateKey(privateKey, options.DataDir);
        ProcessExitSource processExitSource = new(shutdownSource.Token);

        BootnodeKademliaBucketRegistry bucketRegistry = new();
        DiscoveryContainer.ConfigureNetworkBuffers();
        await using IContainer container = await DiscoveryContainer.BuildAsync(options, logManager, nodeKey, processExitSource, bucketRegistry, shutdownSource.Token);
        IDiscoveryApp discoveryApp = container.Resolve<IDiscoveryApp>();
        BootnodeDiscoverySource[] discoverySources = ResolveDiscoverySources(container, options.DiscoveryVersion);
        INodeRecordProvider nodeRecordProvider = container.Resolve<INodeRecordProvider>();
        INetworkConfig networkConfig = container.Resolve<INetworkConfig>();
        NetworkNode[] configuredBootnodes = networkConfig.Bootnodes;

        DiscoveredNodeStore nodeStore = new();
        BootnodeMetrics metrics = new();
        metrics.UpdateSnapshot(nodeStore.AddConfiguredBootnodes(configuredBootnodes));

        BootnodeIdentity identity = await CreateIdentity(container.Resolve<IEnode>(), nodeRecordProvider, nodeKey, shutdownSource.Token);
        metrics.SetIdentity(identity);
        BootnodeStatus status = CreateStatus(options, identity);

        if (options.WriteAddress)
        {
            Console.WriteLine($"enode: {identity.Enode}");
            Console.WriteLine($"enr:   {identity.Enr}");
        }

        await using BootnodeRuntime runtime = new(discoveryApp, discoverySources, nodeStore, metrics, bucketRegistry, processExitSource, logManager);
        await runtime.StartAsync(shutdownSource.Token);

        await using WebApplication httpApp = BuildHttpApp(options.HttpHost, options.HttpPort, nodeStore, status);
        await using WebApplication metricsApp = BuildMetricsApp(options.MetricsHost, options.MetricsPort);

        await httpApp.StartAsync(shutdownSource.Token);
        await metricsApp.StartAsync(shutdownSource.Token);
        logger.Info($"Bootnode HTTP API listening on http://{options.HttpHost}:{options.HttpPort}");
        logger.Info($"Bootnode metrics listening on http://{options.MetricsHost}:{options.MetricsPort}/metrics");

        try
        {
            await processExitSource.ExitTask.WaitAsync(shutdownSource.Token);
        }
        catch (OperationCanceledException) when (shutdownSource.IsCancellationRequested)
        {
            processExitSource.Exit(ExitCodes.SigInt);
        }

        await Task.WhenAll(httpApp.StopAsync(CancellationToken.None), metricsApp.StopAsync(CancellationToken.None));
        return processExitSource.ExitCode;
    }
    catch (Exception exception)
    {
        logger.Error("Bootnode failed.", exception);
        return 1;
    }
    finally
    {
        if (cancelHandler is not null)
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        LogManager.Shutdown();
    }
});

return await rootCommand.Parse(args).InvokeAsync();

BootnodeOptions CreateOptions(ParseResult parseResult)
{
    string dataDir = Path.GetFullPath(parseResult.GetRequiredValue(dataDirOption));
    string[] bootnodes = SplitBootnodes(parseResult.GetRequiredValue(bootnodesOption));
    string? localIp = parseResult.GetValue(localIpOption);
    string? externalIp = parseResult.GetValue(externalIpOption);
    string? externalIpV4 = parseResult.GetValue(externalIpV4Option);
    string? externalIpV6 = parseResult.GetValue(externalIpV6Option);
    int discoveryPort = parseResult.GetRequiredValue(discoveryPortOption);
    ApplyAddr(parseResult.GetValue(addrOption), ref localIp, ref discoveryPort);
    string httpHost = parseResult.GetRequiredValue(httpHostOption);
    int httpPort = parseResult.GetRequiredValue(httpPortOption);
    string metricsHost = parseResult.GetRequiredValue(metricsHostOption);
    int metricsPort = parseResult.GetRequiredValue(metricsPortOption);
    BootnodeOptionValidation.ValidatePort("--discovery-port", discoveryPort);
    BootnodeOptionValidation.ValidatePort("--http-port", httpPort);
    BootnodeOptionValidation.ValidatePort("--metrics-port", metricsPort);
    BootnodeOptionValidation.ValidateLogLevel("--log-level", parseResult.GetRequiredValue(logLevelOption));
    BootnodeOptionValidation.ValidateExternalIp("--external-ip", externalIp, expectedFamily: null);
    BootnodeOptionValidation.ValidateExternalIp("--external-ip-v4", externalIpV4, AddressFamily.InterNetwork);
    BootnodeOptionValidation.ValidateExternalIp("--external-ip-v6", externalIpV6, AddressFamily.InterNetworkV6);
    BootnodeOptionValidation.ValidateHost("--http-host", httpHost);
    BootnodeOptionValidation.ValidateHost("--metrics-host", metricsHost);
    if (httpPort == metricsPort)
    {
        throw new ArgumentException("--http-port and --metrics-port must be different.");
    }

    int activeDiscoveryJobs = parseResult.GetRequiredValue(activeDiscoveryJobsOption);
    int bucketSize = parseResult.GetRequiredValue(bucketSizeOption);
    int concurrency = parseResult.GetRequiredValue(concurrencyOption);
    int discoveryIntervalMs = parseResult.GetRequiredValue(discoveryIntervalOption);
    BootnodeOptionValidation.ValidateNonNegative("--active-discovery-jobs", activeDiscoveryJobs);
    BootnodeOptionValidation.ValidatePositive("--bucket-size", bucketSize);
    BootnodeOptionValidation.ValidatePositive("--concurrency", concurrency);
    BootnodeOptionValidation.ValidatePositive("--discovery-interval-ms", discoveryIntervalMs);

    string? privateKeyFile = parseResult.GetValue(privateKeyFileOption);
    if (string.IsNullOrWhiteSpace(privateKeyFile))
    {
        privateKeyFile = Path.Combine(dataDir, "bootnode.key");
    }

    return new BootnodeOptions
    {
        DataDir = dataDir,
        DiscoveryPort = discoveryPort,
        HttpHost = httpHost,
        HttpPort = httpPort,
        MetricsHost = metricsHost,
        MetricsPort = metricsPort,
        DiscoveryVersion = ParseDiscoveryVersion(parseResult.GetRequiredValue(protocolsOption)),
        ActiveDiscovery = parseResult.GetRequiredValue(activeDiscoveryOption),
        ActiveDiscoveryJobs = activeDiscoveryJobs,
        BucketSize = bucketSize,
        Concurrency = concurrency,
        DiscoveryIntervalMs = discoveryIntervalMs,
        LocalIp = localIp,
        ExternalIp = externalIp,
        ExternalIpV4 = externalIpV4,
        ExternalIpV6 = externalIpV6,
        Bootnodes = bootnodes,
        UseDefaultDiscv5Bootnodes = parseResult.GetRequiredValue(useDefaultDiscv5BootnodesOption),
        LogLevel = parseResult.GetRequiredValue(logLevelOption),
        LogFile = parseResult.GetValue(logFileOption),
        PrivateKey = parseResult.GetValue(privateKeyOption),
        PrivateKeyFile = privateKeyFile,
        GenKey = parseResult.GetRequiredValue(genKeyOption),
        WriteAddress = parseResult.GetRequiredValue(writeAddressOption)
    };
}

static string[] SplitBootnodes(string[] rawBootnodes)
{
    List<string> bootnodes = [];
    for (int i = 0; i < rawBootnodes.Length; i++)
    {
        string[] split = rawBootnodes[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int j = 0; j < split.Length; j++)
        {
            bootnodes.Add(split[j]);
        }
    }

    return [.. bootnodes];
}

static void ApplyAddr(string? addr, ref string? localIp, ref int discoveryPort)
{
    if (string.IsNullOrWhiteSpace(addr))
    {
        return;
    }

    string value = addr.Trim();
    if (value.StartsWith(':'))
    {
        discoveryPort = int.Parse(value[1..]);
        return;
    }

    if (!IPEndPoint.TryParse(value, out IPEndPoint? endpoint))
    {
        throw new ArgumentException($"Unsupported --addr value '{addr}'. Use ':port' or 'ip:port'.");
    }

    localIp = endpoint.Address.ToString();
    discoveryPort = endpoint.Port;
}

static DiscoveryVersion ParseDiscoveryVersion(string value)
{
    string normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "v4" or "discv4" => DiscoveryVersion.V4,
        "v5" or "discv5" => DiscoveryVersion.V5,
        "all" or "v4,v5" or "v5,v4" => DiscoveryVersion.All,
        _ => throw new ArgumentException($"Unsupported --protocols value '{value}'. Use v4, v5, or all.")
    };
}

static int GenerateKey(BootnodeOptions options)
{
    using PrivateKeyGenerator generator = new();
    using PrivateKey privateKey = generator.Generate();

    if (options.PrivateKeyFile is null)
    {
        Console.WriteLine(privateKey.ToString());
        return 0;
    }

    string? directory = Path.GetDirectoryName(options.PrivateKeyFile);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    if (File.Exists(options.PrivateKeyFile))
    {
        Console.Error.WriteLine($"Key file already exists: {options.PrivateKeyFile}");
        return 1;
    }

    WritePrivateKey(options.PrivateKeyFile, privateKey.ToString());
    Console.WriteLine($"Generated node key: {options.PrivateKeyFile}");
    return 0;
}

static PrivateKey LoadOrCreatePrivateKey(BootnodeOptions options, NethermindLogger logger)
{
    if (!string.IsNullOrWhiteSpace(options.PrivateKey))
    {
        return new PrivateKey(options.PrivateKey);
    }

    if (options.PrivateKeyFile is not null && File.Exists(options.PrivateKeyFile))
    {
        string keyText = File.ReadAllText(options.PrivateKeyFile).Trim();
        return new PrivateKey(keyText);
    }

    using PrivateKeyGenerator generator = new();
    PrivateKey generated = generator.Generate();

    if (options.PrivateKeyFile is not null)
    {
        string? directory = Path.GetDirectoryName(options.PrivateKeyFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WritePrivateKey(options.PrivateKeyFile, generated.ToString());
        logger.Info($"Generated node key: {options.PrivateKeyFile}");
    }

    return generated;
}

static void WritePrivateKey(string path, string key)
{
    FileStreamOptions options = new()
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None
    };

    if (!OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    using FileStream stream = new(path, options);
    using StreamWriter writer = new(stream);
    writer.Write(key);

    SetRestrictiveKeyFileMode(path);
}

static void SetRestrictiveKeyFileMode(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

static async Task<BootnodeIdentity> CreateIdentity(
    IEnode enode,
    INodeRecordProvider nodeRecordProvider,
    IProtectedPrivateKey nodeKey,
    CancellationToken cancellationToken)
{
    NodeRecord nodeRecord = await nodeRecordProvider.GetCurrentAsync(cancellationToken);
    return new BootnodeIdentity(
        enode.ToString() ?? string.Empty,
        nodeRecord.ToString(),
        nodeRecord.EnrSequence,
        nodeKey.PublicKey.ToString(false),
        nodeKey.PublicKey.Address.ToString());
}

static BootnodeStatus CreateStatus(BootnodeOptions options, BootnodeIdentity identity)
{
    List<string> protocols = [];
    if ((options.DiscoveryVersion & DiscoveryVersion.V4) != 0)
    {
        protocols.Add("discv4");
    }

    if ((options.DiscoveryVersion & DiscoveryVersion.V5) != 0)
    {
        protocols.Add("discv5");
    }

    return new BootnodeStatus(identity, [.. protocols], options.ActiveDiscovery, options.DiscoveryPort, options.HttpPort, options.MetricsPort);
}

static BootnodeDiscoverySource[] ResolveDiscoverySources(IContainer container, DiscoveryVersion discoveryVersion)
{
    List<BootnodeDiscoverySource> sources = new(2);
    if ((discoveryVersion & DiscoveryVersion.V4) != 0)
    {
        sources.Add(new BootnodeDiscoverySource("discv4", container.Resolve<DiscoveryApp>()));
    }

    if ((discoveryVersion & DiscoveryVersion.V5) != 0)
    {
        sources.Add(new BootnodeDiscoverySource("discv5", container.Resolve<DiscoveryV5App>()));
    }

    return [.. sources];
}

static WebApplication BuildHttpApp(string httpHost, int httpPort, DiscoveredNodeStore nodeStore, BootnodeStatus status)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://{httpHost}:{httpPort}");
    builder.Logging.ClearProviders();
    builder.Services.AddRouting();

    WebApplication app = builder.Build();
    app.UseRouting();

    app.MapGet("/", () => Results.Json(status.CreateStatus(nodeStore.CreateSnapshot())));
    app.MapGet("/status", () => Results.Json(status.CreateStatus(nodeStore.CreateSnapshot())));
    app.MapGet("/identity", () => Results.Json(status.Identity));
    app.MapGet("/nodes/active", (int? offset, int? limit) => CreateNodesResult(nodeStore, activeOnly: true, offset, limit));
    app.MapGet("/nodes/all", (int? offset, int? limit) => CreateNodesResult(nodeStore, activeOnly: false, offset, limit));
    app.MapPost("/rpc", (JsonElement payload) => JsonRpcEndpoint.Handle(payload, nodeStore, status));
    app.MapPost("/", (JsonElement payload) => JsonRpcEndpoint.Handle(payload, nodeStore, status));

    return app;
}

static IResult CreateNodesResult(DiscoveredNodeStore nodeStore, bool activeOnly, int? offset, int? limit)
{
    int resolvedOffset = offset ?? 0;
    int resolvedLimit = limit ?? DiscoveredNodeStore.DefaultNodePageSize;
    if (!DiscoveredNodeStore.TryValidatePagination(resolvedOffset, resolvedLimit, out string error))
    {
        return Results.BadRequest(new { Error = error });
    }

    NodeDto[] nodes = activeOnly
        ? nodeStore.GetActiveNodes(resolvedOffset, resolvedLimit)
        : nodeStore.GetAllNodes(resolvedOffset, resolvedLimit);
    return Results.Json(nodes);
}

static WebApplication BuildMetricsApp(string metricsHost, int metricsPort)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://{metricsHost}:{metricsPort}");
    builder.Logging.ClearProviders();
    builder.Services.AddRouting();

    WebApplication app = builder.Build();
    app.UseRouting();
    app.UseMetricServer("/metrics");

    return app;
}
