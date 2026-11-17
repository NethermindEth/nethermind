// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Threading;
using Nethermind.Stats.Model;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using NSubstitute;
using NUnit.Framework;
using Nethermind.TxPool;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

internal static class PooledTransactionRetryCache
{
    internal static IContainer CreateRequestedProgressPool(TimeProvider clock, TxPoolConfig? config = null)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, UInt256.MaxValue);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(London.Instance))
            .AddSingleton(state)
            .AddSingleton<ITxPoolConfig>(config ?? new TxPoolConfig())
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2).TestObject)
            .AddSingleton<IChainHeadInfoProvider>(context => new ChainHeadInfoProvider(
                context.Resolve<IChainHeadSpecProvider>(), context.Resolve<IBlockTree>(), state)
            { HasSynced = true })
            .AddSingleton<ITxPool>(context => new TxPool.TxPool(clock,
                context.Resolve<IEthereumEcdsa>(), context.Resolve<IBlobTxStorage>(),
                context.Resolve<IChainHeadInfoProvider>(), context.Resolve<ITxPoolConfig>(),
                context.Resolve<ITxValidator>(), context.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey),
                context.Resolve<ILogManager>(), context.Resolve<IComparer<Transaction>>(),
                context.Resolve<FrameTxWidthLedger>(), context.Resolve<ITxGossipPolicy>()));
        return builder.Build();
    }

    internal static async Task VerifyFirstPooledSendCannotOutliveDisposal(ProtocolHandlerBase handler, ISession session,
        ManualTimeProvider clock, Action send, bool closing, Func<TimeProvider, ProtocolHandlerBase> createFresh, Action<PooledTransactionRequestMessage> sendFresh)
    {
        Node node = session.Node;
        TaskCompletionSource reserving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim resume = new(false);
        int senderThread = 0;
        session.Node.Returns(_ =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref senderThread))
            {
                reserving.TrySetResult();
                resume.Wait();
            }
            return node;
        });
        Task sending = Task.Run(() =>
        {
            Volatile.Write(ref senderThread, Environment.CurrentManagedThreadId);
            send();
        });
        try
        {
            await reserving.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.IsClosing.Returns(closing);
            handler.Dispose();
        }
        finally
        {
            resume.Set();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.DueTimes, Is.Empty);
            Assert.That(SentRequestHashes(), Is.Empty);
            session.DidNotReceive().InitiateDisconnect(Arg.Any<Nethermind.Stats.Model.DisconnectReason>(), Arg.Any<string>());
        }
        session.IsClosing.Returns(false);
        using ProtocolHandlerBase fresh = createFresh(new ManualTimeProvider());
        Hash256[] expected = Enumerable.Range(0, 32).Select(static index => new Hash256(index.ToString("X64"))).ToArray();
        foreach (Hash256 hash in expected) sendFresh(PooledTransactionRequestMessage.New(hash));
        ValueHash256[][] requests = SentRequestHashes();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(32));
            Assert.That(requests.SelectMany(static hashes => hashes),
                Is.EquivalentTo(expected.Select(static hash => hash.ValueHash256)));
            session.DidNotReceive().InitiateDisconnect(Arg.Any<Nethermind.Stats.Model.DisconnectReason>(), Arg.Any<string>());
        }

        ValueHash256[][] SentRequestHashes() => session.ReceivedCalls()
            .SelectMany(static call => call.GetArguments().OfType<P2PMessage>())
            .Select(static message => message switch
            {
                Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage legacy => legacy.Hashes.AsSpan().ToArray(),
                Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage modern => modern.EthMessage.Hashes.AsSpan().ToArray(),
                _ => null
            }).OfType<ValueHash256[]>().ToArray();
    }

    internal static RetryCache<PooledTransactionRequestMessage, ValueHash256> CreateRetryCache(TimeProvider clock) =>
        new(LimboLogs.Instance, clock, timeoutMs: 500,
            overflowRequestLimit: RetryCache<PooledTransactionRequestMessage, ValueHash256>.DefaultOverflowRequestLimit);

    internal static bool TryDeferRequest(RetryCache<PooledTransactionRequestMessage, ValueHash256> retry,
        ValueHash256 hash, IMessageHandler<PooledTransactionRequestMessage> handler) => retry.TryDeferRequest(in hash, handler);

    internal static void ProcessRetryTick(RetryCache<PooledTransactionRequestMessage, ValueHash256> retry) => retry.ProcessRetryTick();

    internal static async Task VerifyZeroSendRetriesOriginalHashes(ProtocolHandlerBase handler, ISession session,
        ITxPool pool, ManualTimeProvider requestClock, int hashCount, Action<IOwnedReadOnlyList<Hash256>> announce, Action<long?> reply)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<PooledTransactionRequestMessage, ValueHash256> retry = CreateRetryCache(clock);
        pool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(call =>
            {
                ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                return retry.Announced(in hash, call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1));
            });
        pool.TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(call => TryDeferRequest(retry, call.ArgAt<ValueHash256>(0),
                call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1)));
        bool writable = false;
        int attempts = 0;
        List<(long? RequestId, ValueHash256[] Hashes)> sent = [];
        session.DeliverMessage(Arg.Any<Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage>())
            .Returns(call => Send(call.Arg<P2PMessage>()));
        session.DeliverMessage(Arg.Any<Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>())
            .Returns(call => Send(call.Arg<P2PMessage>()));
        using ArrayPoolList<Hash256> hashes = new(hashCount);
        for (int index = 0; index < hashCount; index++) hashes.Add(new Hash256(index.ToString("X64")));
        ValueHash256[] expected = hashes.Select(static hash => hash.ValueHash256).ToArray();
        int retryTicks = (hashCount + RetryWorkBatchSize - 1) / RetryWorkBatchSize;
        announce(hashes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts, Is.EqualTo(retryTicks));
            Assert.That(sent, Is.Empty);
        }
        for (int tick = 0; tick < retryTicks; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            requestClock.Advance(TimeSpan.FromMilliseconds(100));
            ProcessRetryTick(retry);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts, Is.EqualTo(2 * retryTicks), "zero sends during retry must preserve the source again");
            Assert.That(sent, Is.Empty);
        }
        writable = true;
        int answered = 0;
        for (int tick = 0; tick < retryTicks; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            requestClock.Advance(TimeSpan.FromMilliseconds(100));
            ProcessRetryTick(retry);
            foreach ((long? RequestId, ValueHash256[] Hashes) request in sent.Skip(answered)) reply(request.RequestId);
            answered = sent.Count;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts, Is.EqualTo(3 * retryTicks));
            Assert.That(sent.Count, Is.EqualTo(retryTicks));
            Assert.That(sent.SelectMany(static request => request.Hashes),
                Is.EquivalentTo(expected));
            session.DidNotReceive().InitiateDisconnect(Arg.Any<Nethermind.Stats.Model.DisconnectReason>(), Arg.Any<string>());
        }

        int Send(P2PMessage message)
        {
            try
            {
                attempts++;
                if (!writable) return 0;
                (long? id, IOwnedReadOnlyList<ValueHash256> values) = message switch
                {
                    Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage legacy => ((long?)null, legacy.Hashes),
                    Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage modern => ((long?)modern.RequestId, modern.EthMessage.Hashes),
                    _ => throw new InvalidOperationException("unexpected pooled request")
                };
                sent.Add((id, values.AsSpan().ToArray()));
                return 1;
            }
            finally { message.Dispose(); }
        }
    }

    internal static int RetryWorkBatchSize => RetryCache<PooledTransactionRequestMessage, ValueHash256>.MaxRetryResourcesPerHandlerPerTick;
}
