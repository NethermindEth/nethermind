// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Runner;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Api;
using Testably.Abstractions;

namespace Nethermind.FFI;

/// <summary>Runs the node for the native host and gives native calls access to it.</summary>
/// <remarks>
/// The node is considered ready once <see cref="EthereumRunner.Start"/> returns, i.e. every initialization step has
/// completed. Native calls hold a read lock while they use the node and stopping takes the write lock, so the runner is
/// never torn down under an in-flight call.
/// </remarks>
public static class FfiHost
{
    /// <summary>Makes <see cref="PathUtils"/> resolve resources next to this assembly instead of the host executable.</summary>
    private const string HostedProperty = "Nethermind.Hosted";

    private static readonly ReaderWriterLockSlim Lock = new();
    private static readonly TaskCompletionSource Signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FfiNode? _node;
    private static volatile bool _stopping;
    private static ProcessExitSource? _exitSource;
    private static Task<int>? _run;

    /// <summary>Starts the node in the background.</summary>
    /// <param name="config">A config name from the <c>configs</c> directory (e.g. <c>mainnet</c>) or a path to a config file.</param>
    /// <param name="overrides">Config values keyed by <c>Category.Name</c>, taking precedence over the environment and the config file.</param>
    public static void Start(string config, Dictionary<string, string> overrides)
    {
        ProcessExitSource exitSource = new(CancellationToken.None);
        if (Interlocked.CompareExchange(ref _exitSource, exitSource, null) is not null)
            throw new InvalidOperationException("The node can only be started once per process.");

        exitSource.Token.Register(MarkStopping);
        _run = Task.Run(() => RunAsync(config, overrides, exitSource));
    }

    public static FfiStatus WaitReady(int timeoutMs) =>
        !Signalled.Task.Wait(timeoutMs) ? FfiStatus.NotReady
        : _stopping ? FfiStatus.Stopped
        : FfiStatus.Ok;

    /// <summary>Runs <paramref name="action"/> against the node, unless it is not ready yet or stopping.</summary>
    public static FfiStatus Use(Func<FfiNode, FfiStatus> action)
    {
        Lock.EnterReadLock();
        try
        {
            return _node is null ? (_stopping ? FfiStatus.Stopped : FfiStatus.NotReady) : action(_node);
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static void Stop(int exitCode) => _exitSource?.Exit(exitCode);

    /// <summary>Waits for the node to stop and returns its exit code.</summary>
    public static int Join() => _run?.GetAwaiter().GetResult() ?? ExitCodes.GeneralError;

    private static async Task<int> RunAsync(string config, Dictionary<string, string> overrides, ProcessExitSource exitSource)
    {
        ILogger logger = new(SimpleConsoleLogger.Instance);
        EthereumRunner? runner = null;
        try
        {
            (runner, logger) = await CreateRunner(config, overrides, exitSource);
            await runner.Start(exitSource.Token);

            using ILifetimeScope scope = FfiNode.CreateScope(runner.LifetimeScope);
            SetNode(scope.Resolve<FfiNode>());
            Signalled.TrySetResult();
            await exitSource.ExitTask;
            SetNode(null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (logger.IsError) logger.Error("A critical error has occurred", e);
            exitSource.Exit(e is IExceptionWithExitCode withExitCode ? withExitCode.ExitCode : ExitCodes.GeneralError);
        }
        finally
        {
            MarkStopping();
            if (runner is not null) await runner.StopAsync();
            NLogManager.Shutdown();
        }

        return exitSource.ExitCode;
    }

    /// <summary>A trimmed-down version of the executable's bootstrap: config file plus overrides, file logging, embedded plugins.</summary>
    private static async Task<(EthereumRunner, ILogger)> CreateRunner(string config, Dictionary<string, string> overrides, ProcessExitSource exitSource)
    {
        AppContext.SetData(HostedProperty, "true");
        // ProductInfo reads the entry assembly, which a native host does not have.
        Assembly.SetEntryAssembly(typeof(EthereumRunner).Assembly);
        BlocksConfig.SetDefaultExtraDataWithVersion();
        NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());

        PluginLoader pluginLoader = new("plugins", new RealFileSystem(), NullLogger.Instance, NethermindPlugins.EmbeddedPlugins);
        pluginLoader.Load();
        TypeDiscovery.Initialize(typeof(INethermindPlugin));

        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(overrides));
        configProvider.AddSource(new EnvConfigSource());
        configProvider.AddSource(new JsonConfigSource(ResolveConfigFile(config)));
        configProvider.Initialize();
        pluginLoader.OrderPlugins(configProvider.GetConfig<IPluginConfig>());

        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);
        ApiBuilder apiBuilder = new(exitSource, configProvider, logManager);
        IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(configProvider, apiBuilder.ChainSpec);
        return (apiBuilder.CreateEthereumRunner(plugins, command: null), logManager.GetClassLogger(typeof(FfiHost)));
    }

    private static string ResolveConfigFile(string config) =>
        !string.IsNullOrEmpty(Path.GetDirectoryName(config)) ? config
        : Path.Join("configs".GetApplicationResourcePath(), Path.HasExtension(config) ? config : $"{config}.json");

    private static void SetNode(FfiNode? node)
    {
        Lock.EnterWriteLock();
        try
        {
            _node = node;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    private static void MarkStopping()
    {
        _stopping = true;
        Signalled.TrySetResult();
    }
}
