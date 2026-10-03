// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// <c>status</c> v2 falls back to v1 only when v2 failed as an exchange or went unanswered, which is how a protocol the peer
/// does not speak surfaces once the peer answers <c>na</c>; a local fault or the caller's cancellation is no reason to retry.
/// </summary>
public class StatusFallbackTests
{
    private static readonly StatusMessageV2 V1Answer = new() { ForkDigest = [1, 2, 3, 4], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero, HeadSlot = 77 };

    public enum V2Failure
    {
        ExchangeFailure,
        ExchangeFailureAsTheSessionDeliversIt,
        Unanswered,
        LocalFault,
        LocalFaultAsTheSessionDeliversIt,
        CallerCancelled,
    }

    [TestCase(V2Failure.ExchangeFailure, true)]
    [TestCase(V2Failure.ExchangeFailureAsTheSessionDeliversIt, true)]
    [TestCase(V2Failure.Unanswered, true)]
    [TestCase(V2Failure.LocalFault, false)]
    [TestCase(V2Failure.LocalFaultAsTheSessionDeliversIt, false)]
    [TestCase(V2Failure.CallerCancelled, false)]
    public async Task Status_v2_falls_back_to_v1_only_for_a_failed_or_unanswered_exchange(V2Failure failure, bool fallsBack)
    {
        using CancellationTokenSource caller = new();
        Exception v2Error = failure switch
        {
            V2Failure.ExchangeFailure => new Eth2ReqRespException("Peer responded with error code 2"),
            V2Failure.ExchangeFailureAsTheSessionDeliversIt => new AggregateException(new Eth2ReqRespException("Peer responded with error code 2")),
            V2Failure.Unanswered or V2Failure.CallerCancelled => new TaskCanceledException(),
            V2Failure.LocalFault => new IOException("Failed to half-close the request stream"),
            V2Failure.LocalFaultAsTheSessionDeliversIt => new AggregateException(new IOException("Failed to half-close the request stream")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        if (failure == V2Failure.CallerCancelled)
        {
            await caller.CancelAsync();
        }

        (Exception? thrown, StatusMessageV2? answer, ISession session) = await RequestStatusAsync(v2Error, caller.Token);

        int v1Dials = session.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(ISession.DialAsync) && c.GetMethodInfo().GetGenericArguments()[0] == typeof(StatusProtocolV1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(v1Dials, Is.EqualTo(fallsBack ? 1 : 0), "v1 dials");
            Assert.That(answer?.HeadSlot, Is.EqualTo(fallsBack ? V1Answer.HeadSlot : null), "the v1 answer is returned when v1 is tried");
            Assert.That(thrown, fallsBack ? Is.Null : Is.SameAs(v2Error), "a failure that is no reason to try v1 reaches the caller unchanged");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_status_v2_answer_that_fails_to_decode_is_an_exchange_failure_and_falls_back_to_v1(CancellationToken token)
    {
        Eth2ReqRespException decodeFailure = Assert.ThrowsAsync<Eth2ReqRespException>(() => DialStatusV2AnsweredWithAsync(new byte[91], token))!;
        Assert.That(decodeFailure.Message, Does.Contain("92 bytes"), "the documented decode failure of a short v2 status");

        (Exception? thrown, StatusMessageV2? answer, _) = await RequestStatusAsync(new AggregateException(decodeFailure), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(answer?.HeadSlot, Is.EqualTo(V1Answer.HeadSlot));
        }
    }

    [Test]
    public void Published_status_uses_the_wall_clock_digest_at_the_fork_boundary([Values] bool earliestSource, [Values] bool bpo)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        ulong epoch = bpo ? spec.BlobSchedule[0].Epoch : spec.FuluForkEpoch;
        ulong seconds = spec.GenesisTime + epoch * spec.SlotsPerEpoch * spec.SecondsPerSlot;
        ManualTimestamper time = new(DateTime.UnixEpoch.AddSeconds(seconds - 1));
        BeaconChainStatusHolder holder = new(spec, time);
        Hash256 head = Keccak.Compute("head");
        holder.Publish(new StatusMessageV2
        {
            ForkDigest = ForkDigest.Compute(spec, epoch - 1),
            HeadRoot = head,
            HeadSlot = 42,
            FinalizedRoot = Hash256.Zero,
            EarliestAvailableSlot = 10,
        }, head);
        if (earliestSource) holder.EarliestAvailableSlotSource = () => 11;
        Assert.That(holder.CurrentStatus.ForkDigest, Is.EqualTo(ForkDigest.Compute(spec, epoch - 1)));
        time.Set(DateTime.UnixEpoch.AddSeconds(seconds));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder.CurrentStatus.ForkDigest, Is.EqualTo(ForkDigest.Compute(spec, epoch)));
            Assert.That(holder.CurrentHead.Status.ForkDigest, Is.EqualTo(ForkDigest.Compute(spec, epoch)));
            Assert.That(holder.CurrentHead.FullHeadRoot, Is.EqualTo(head));
            Assert.That(holder.CurrentStatus.HeadSlot, Is.EqualTo(42));
            Assert.That(holder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(earliestSource ? 11 : 10));
        }
    }

    private static async Task<(Exception? Thrown, StatusMessageV2? Answer, ISession Session)> RequestStatusAsync(Exception v2Error, CancellationToken token)
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(Task.FromException<StatusMessageV2>(v2Error));
        session.DialAsync<StatusProtocolV1, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(Task.FromResult(V1Answer));
        await using BeaconP2P p2p = CreateNode();
        try
        {
            return (null, await p2p.RequestStatusAsync(session, token), session);
        }
        catch (Exception e)
        {
            return (e, null, session);
        }
    }

    // The listener side answers any request with one success chunk carrying the given payload.
    private static async Task<StatusMessageV2> DialStatusV2AnsweredWithAsync(byte[] payload, CancellationToken token)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task answering = Task.Run(async () =>
        {
            Stream listener = new ChannelStreamAdapter(channel.Reverse);
            try
            {
                await ReqRespFraming.ReadRequestAsync(listener, 92, token);
                await ReqRespFraming.WriteResponseChunkAsync(listener, ReqRespFraming.ResponseCode.Success, default, payload, token);
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);

        try
        {
            return await new StatusProtocolV2(new BeaconChainStatusHolder(BeaconChainSpec.Mainnet, Timestamper.Default)).DialAsync(channel, context, V1Answer);
        }
        finally
        {
            await answering.WaitAsync(token);
        }
    }

    private static BeaconP2P CreateNode() =>
        new(new BeaconChainConfig { P2PPort = 0 }, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new BeaconChainStatusHolder(BeaconChainSpec.Mainnet, Timestamper.Default), new LocalMetadataSource(), new DataColumnSidecarPool(),
            new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
}
