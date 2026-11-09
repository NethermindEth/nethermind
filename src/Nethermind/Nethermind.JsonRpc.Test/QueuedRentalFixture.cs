// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// The node's rental chain (service, provider, lazy pool, bounded pool) over one exclusive module, which a test holds so
/// that its requests queue. The fixture itself sits between the lazy and the bounded pool to see rentals queue.
/// </summary>
public sealed class QueuedRentalFixture : IRpcModulePool<QueuedRentalFixture.IProbeRpcModule>, IRpcModuleFactory<QueuedRentalFixture.IProbeRpcModule>, IDisposable
{
    public const string Method = "probe_exclusive";
    public const string SerialMethod = "probe_serial";
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    private readonly BoundedModulePool<IProbeRpcModule> _pool;
    private readonly SemaphoreSlim _queued = new(0);
    private readonly GCKeeper _gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
    private int _invocations;

    public QueuedRentalFixture(JsonRpcConfig? config = null, ILogManager? logManager = null)
    {
        _pool = new BoundedModulePool<IProbeRpcModule>(this, 1, Timeout.Infinite, new RpcLimits(queuedLimit: 8));
        Config = config ?? new JsonRpcConfig();
        RpcModuleProvider provider = new(new RealFileSystem(), Config, new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register(new LazyModulePool<IProbeRpcModule>(new Lazy<IRpcModulePool<IProbeRpcModule>>(() => this)));
        Service = new JsonRpcService(provider, logManager ?? LimboLogs.Instance, Config, _gcKeeper);
    }

    public JsonRpcConfig Config { get; }
    public JsonRpcService Service { get; }
    public int Invocations => Volatile.Read(ref _invocations);
    public IRpcModuleFactory<IProbeRpcModule> Factory => this;

    public static string Request(string id = "1", string method = Method) => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":[]}""";

    public JsonRpcProcessor CreateProcessor(ILogManager? logManager = null, params IBatchConcurrencyStrategy[] strategies) =>
        new(Service, Config, new RealFileSystem(), logManager ?? LimboLogs.Instance, null, strategies);

    /// <summary>Rents the only module, so every request after it queues.</summary>
    public IProbeRpcModule Hold() => _pool.GetModule(false).Result;

    public async Task WaitUntilQueued(int count)
    {
        for (int i = 0; i < count; i++) Assert.That(await _queued.WaitAsync(Watchdog), Is.True, $"only {i} of {count} requests queued");
    }

    /// <summary>Returns the held module and checks that the pool hands it out again, so no abandoned rental took it.</summary>
    public async Task ReleaseAndAssertUsable(IProbeRpcModule held)
    {
        _pool.ReturnModule(held);
        _pool.ReturnModule(await _pool.GetModule(false).WaitAsync(Watchdog));
    }

    public Task<IProbeRpcModule> GetModule(bool canBeShared) => GetModule(canBeShared, default);

    public Task<IProbeRpcModule> GetModule(bool canBeShared, CancellationToken cancellationToken)
    {
        Task<IProbeRpcModule> rental = _pool.GetModule(canBeShared, cancellationToken);
        if (!rental.IsCompleted) _queued.Release();
        return rental;
    }

    public void ReturnModule(IProbeRpcModule module) => _pool.ReturnModule(module);

    public IProbeRpcModule Create() => new Probe(this);

    public void Dispose()
    {
        _gcKeeper.Dispose();
        _queued.Dispose();
    }

    [RpcModule(ModuleType.Eth)]
    public interface IProbeRpcModule : IRpcModule
    {
        [JsonRpcMethod(IsSharable = false)]
        ResultWrapper<int> probe_exclusive();

        [JsonRpcMethod(IsSharable = false)]
        ResultWrapper<int> probe_serial();
    }

    private sealed class Probe(QueuedRentalFixture fixture) : IProbeRpcModule
    {
        public ResultWrapper<int> probe_exclusive() => ResultWrapper<int>.Success(Interlocked.Increment(ref fixture._invocations));
        public ResultWrapper<int> probe_serial() => probe_exclusive();
    }
}
