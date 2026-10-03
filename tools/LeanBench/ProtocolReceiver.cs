// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
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
    private sealed class Scheduler : IBackgroundTaskScheduler
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        public bool TryScheduleTask<T>(T request, Func<T, CancellationToken, Task> execute, TimeSpan? timeout = null)
            where T : notnull, IBackgroundTaskRequest<T>
        {
            _tasks.Add(Task.Run(() => execute(request, CancellationToken.None)));
            return true;
        }
        public async Task DrainAsync(CancellationToken token)
        {
            Task[] tasks;
            do
            {
                tasks = _tasks.ToArray();
                await Task.WhenAll(tasks).WaitAsync(token);
            } while (_tasks.Count != tasks.Length);
        }
    }

    private readonly Scheduler _scheduler;
    public BasicTestBlockchain Chain { get; }
    public LeanProtocolHandler Handler { get; }
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
        Scheduler scheduler = new();
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier)
            .AddSingleton<IBackgroundTaskScheduler>(scheduler));
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
