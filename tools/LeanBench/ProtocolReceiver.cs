// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Tools.LeanBench;

/// <summary>Runs the actual lean/1 receive queue and production pool admission.</summary>
public sealed class ProtocolReceiver : IDisposable
{
    private sealed class Scheduler(IBackgroundTaskScheduler backend) : IBackgroundTaskScheduler
    {
        private readonly Lock _gate = new();
        private int _pending;
        private TaskCompletionSource? _drained;
        private Exception? _failure;

        private sealed class Request<T>(T value, Action complete) : IBackgroundTaskRequest<Request<T>>, IDisposable
            where T : notnull, IBackgroundTaskRequest<T>
        {
            public static int TaskId => T.TaskId;
            public T Value { get; } = value;
            private int _completed;
            private int _disposed;
            public void Complete()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0) complete();
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { Value.TryDispose(); }
                finally { Complete(); }
            }
        }

        public string BackendType => backend.GetType().FullName!;

        public bool TryScheduleTask<T>(T request, Func<T, CancellationToken, Task> execute, TimeSpan? timeout = null)
            where T : notnull, IBackgroundTaskRequest<T>
        {
            lock (_gate)
                if (_pending++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Request<T> tracked = new(request, Complete);
            try
            {
                bool accepted = backend.TryScheduleTask(tracked, async (item, token) =>
                {
                    try { await execute(item.Value, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception exception) { lock (_gate) _failure ??= exception; }
                    finally { item.Complete(); }
                }, timeout);
                if (!accepted) tracked.Complete();
                return accepted;
            }
            catch { tracked.Complete(); throw; }
        }

        private void Complete()
        {
            lock (_gate)
                if (--_pending == 0) _drained!.TrySetResult();
        }

        public async Task DrainAsync(CancellationToken token)
        {
            Task drained;
            lock (_gate) drained = _pending == 0 ? Task.CompletedTask : _drained!.Task;
            await drained.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
                if (_failure is not null) throw new InvalidOperationException("Protocol scheduler task failed", _failure);
        }
    }

    private readonly Scheduler _scheduler;
    public BasicTestBlockchain Chain { get; }
    public LeanProtocolHandler Handler { get; }
    public string SchedulerBackendType => _scheduler.BackendType;
    public string? DisconnectReason { get; private set; }

    private ProtocolReceiver(BasicTestBlockchain chain, LeanProtocolHandler handler, Scheduler scheduler, ISession session)
    {
        Chain = chain;
        Handler = handler;
        _scheduler = scheduler;
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do(call => DisconnectReason = call.ArgAt<string>(1));
        IByteBuffer status = Unpooled.Buffer();
        try
        {
            new LeanStatusMessageSerializer().Serialize(status, new(chain.BlockTree.ChainId,
                chain.BlockTree.Genesis!.Hash!, Eip8288Constants.AggregatedVk.ToArray()));
            Handler.HandleMessage(new ZeroPacket(status) { PacketType = 0, Protocol = "lean" });
        }
        finally { status.Release(); }
    }

    public static async Task<ProtocolReceiver> Create(MeasuredVerifier verifier)
    {
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier)
            .AddDecorator<IBackgroundTaskScheduler>((_, backend) => new Scheduler(backend)));
        Scheduler scheduler = (Scheduler)chain.Container.Resolve<IBackgroundTaskScheduler>();
        if (scheduler.BackendType != typeof(BackgroundTaskScheduler).FullName)
            throw new InvalidOperationException("Expected the production BackgroundTaskScheduler");
        ISession session = Substitute.For<ISession, ILeanBulkSession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
        session.HasAgreedCapability(Arg.Any<Capability>()).Returns(true);
        IProtocolHandlerFactory factory = chain.Container.Resolve<IProtocolHandlerFactory[]>().First(f => f.ProtocolCode == "lean");
        if (!factory.TryCreate(session, Lean1ProtocolHandler.Version, out IProtocolHandler? handler)) throw new InvalidOperationException("lean/1 unavailable");
        return new(chain, (LeanProtocolHandler)handler!, scheduler, session);
    }

    public void Receive(ZeroPacket packet)
    {
        packet.Protocol = "lean";
        Handler.HandleMessage(packet);
    }

    public Task DrainAsync(CancellationToken token) => _scheduler.DrainAsync(token);
    public void Dispose()
    {
        Handler.Dispose();
        Chain.Dispose();
    }
}
