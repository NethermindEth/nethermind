// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public abstract class KademliaDiscoveryApp(
    string description,
    INetworkConfig networkConfig,
    IIPResolver ipResolver,
    IProcessExitSource processExitSource,
    ILogger logger) : IDiscoveryApp, IAsyncDisposable
{
    private readonly string _description = description;
    private readonly INetworkConfig _networkConfig = networkConfig;
    private readonly IIPResolver _ipResolver = ipResolver;
    private readonly CancellationTokenSource _stopCts = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
    private IKademliaNodeSource? _kademliaNodeSource;
    private IKademlia<PublicKey, Node>? _kademlia;
    private Task? _runningTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private readonly object _lifetimeLock = new();
    private int _activationStarted;

    protected ILogger Logger { get; } = logger;

    protected IKademlia<PublicKey, Node> Kademlia => _kademlia ?? throw new InvalidOperationException("Kademlia services were not initialized.");

    public async Task StartAsync()
    {
        try
        {
            await Initialize(_stopCts.Token);
            TryStartActivation();
        }
        catch (Exception e)
        {
            Logger.Error($"Error during {_description} app start process", e);
            throw;
        }
    }

    public Task StopAsync()
    {
        lock (_lifetimeLock)
        {
            return _stopTask ??= StopAsyncInternal();
        }
    }

    private async Task StopAsyncInternal()
    {
        DetachEventHandlers();

        await _stopCts.CancelAsync();

        try
        {
            if (_runningTask is not null)
            {
                await _runningTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (Logger.IsError) Logger.Error($"Error in {_description} task", e);
        }

        try
        {
            await StopAsyncCore();
        }
        finally
        {
            _stopCts.Dispose();
        }

        if (Logger.IsInfo) Logger.Info($"{_description} shutdown complete. Please wait for all components to close");
    }

    string IStoppableService.Description => _description;

    public abstract void InitializeChannel(IChannel channel);

    public virtual void AddNodeToDiscovery(Node node) => Kademlia.AddOrRefresh(node);

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token)
        => (_kademliaNodeSource ?? throw new InvalidOperationException("Kademlia services were not initialized.")).DiscoverNodes(token);

    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            return new ValueTask(_disposeTask ??= DisposeAsyncInternal());
        }
    }

    private async Task DisposeAsyncInternal()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            if (_kademlia is not null)
            {
                _kademlia.OnNodeRemoved -= OnKademliaNodeRemoved;
            }

            await DisposeAsyncCore();
        }
    }

    protected void UseKademliaServices(IKademliaNodeSource kademliaNodeSource, IKademlia<PublicKey, Node> kademlia)
    {
        _kademliaNodeSource = kademliaNodeSource;
        _kademlia = kademlia;
        _kademlia.OnNodeRemoved += OnKademliaNodeRemoved;
    }

    protected virtual async Task Initialize(CancellationToken cancellationToken)
    {
        IIPResolver.NethermindIp ip = await _ipResolver.Resolve(cancellationToken);

        if (Logger.IsDebug) Logger.Debug($"Discovery    : udp://{ip.ExternalIp}:{_networkConfig.DiscoveryPort}");

        ThisNodeInfo.AddInfo("Discovery    :", $"udp://{ip.ExternalIp}:{_networkConfig.DiscoveryPort}");
    }

    protected void OnChannelActivated(object? sender, EventArgs e)
    {
        if (Logger.IsDebug) Logger.Debug("Activated discovery channel.");

        if (_stopCts.IsCancellationRequested)
        {
            return;
        }

        TryStartActivation();
    }

    private void TryStartActivation()
    {
        if (_stopCts.IsCancellationRequested ||
            Interlocked.CompareExchange(ref _activationStarted, 1, 0) != 0)
        {
            return;
        }

        _runningTask = StartActivationAsync(_stopCts.Token);
    }

    protected virtual void DetachEventHandlers()
    {
    }

    protected virtual Task StopAsyncCore() => Task.CompletedTask;

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    protected abstract Task RunDiscoveryAsync(CancellationToken cancellationToken);

    private async Task StartActivationAsync(CancellationToken cancellationToken)
    {
        const string faultMessage = "Cannot activate channel.";

        try
        {
            await Task.Factory.StartNew(static state => ((KademliaDiscoveryApp)state!).ActivateAsync(), this, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            if (!cancellationToken.IsCancellationRequested && Logger.IsDebug) Logger.Debug($"{_description} App initialized.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (Logger.IsInfo) Logger.Info(faultMessage);
            throw;
        }
    }

    private Task ActivateAsync() => ActivateAsync(_stopCts.Token);

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunDiscoveryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (Logger.IsInfo) Logger.Info($"{_description} App stopped");
        }
        catch (Exception e)
        {
            Logger.DebugError($"Error during {_description} initialization", e);
        }
    }

    private void OnKademliaNodeRemoved(object? sender, Node node) => NodeRemoved?.Invoke(sender, new NodeEventArgs(node));
}
