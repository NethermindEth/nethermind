// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Runs <see cref="GossipMessageValidator"/> off the pubsub router's monitor and gives the router each message's verdict once its checks finish.</summary>
/// <remarks>
/// <para>
/// The router calls <see cref="Verify"/> under its monitor for every new message id. It drops a message on a topic this node
/// does not handle there, and otherwise returns <see cref="MessageValidity.Deferred"/>. The router then calls
/// <see cref="ValidateAsync"/> outside its monitor on the delivering peer's read loop, so one peer's messages are checked in
/// order and a slow check delays only that peer. A message whose checks continue in the import pipeline keeps its
/// <see cref="GossipVerdict"/> there, and the router forwards it only once that verdict is <see cref="MessageValidity.Accepted"/>
/// (phase0 p2p-interface.md "Topics and messages": ACCEPT once every validation passed).
/// </para>
/// <para>
/// Past <paramref name="maxPending"/> messages or <paramref name="maxPendingBytes"/> bytes waiting for a verdict, a new message is
/// <see cref="MessageValidity.Throttled"/>: the router caches no id for it, so a later copy is checked, and charges its sender
/// nothing. The byte count covers dispatched messages, so the router's own byte bound is set one RPC higher and never drops one
/// unseen. A verdict not given within <paramref name="timeout"/> is abandoned, which the router treats the same way.
/// </para>
/// </remarks>
internal sealed class DeferredGossipValidation(
    PubsubRouter router,
    GossipMessageValidator validator,
    int maxPending,
    long maxPendingBytes,
    TimeSpan timeout,
    ILogger logger,
    CancellationToken stopping)
{
    private long _pendingBytes;

    /// <summary>The bytes of the messages dispatched for validation whose verdict is not given yet.</summary>
    internal long PendingBytes => Interlocked.Read(ref _pendingBytes);

    /// <summary>The router's synchronous hook: drops a message on a topic this node does not handle and defers the others.</summary>
    public MessageValidity Verify(PeerId source, Message message)
    {
        if (!validator.IsDeferred(message.Topic))
        {
            return validator.Validate(message, GossipVerdict.None);
        }

        // Read under the router's monitor, which the caller holds, so it counts every message deferred before this one.
        if (router.PendingValidationCount >= maxPending || PendingBytes + message.CalculateSize() > maxPendingBytes)
        {
            Interlocked.Increment(ref Metrics.GossipThrottledCount);
            if (logger.IsDebug) logger.Debug($"Throttling gossip on {message.Topic} from {source}: {router.PendingValidationCount} messages and {PendingBytes} bytes await a verdict");
            return MessageValidity.Throttled;
        }

        return MessageValidity.Deferred;
    }

    /// <summary>The router's deferred validator: runs every check that can run now and waits until the verdict is given or abandoned.</summary>
    /// <remarks>The router drops the pending message once the returned task ends, so it ends only after the verdict reached the router.</remarks>
    public async Task ValidateAsync(PeerId source, Message message)
    {
        long size = message.CalculateSize();
        Interlocked.Add(ref _pendingBytes, size);
        GossipVerdict verdict = new(validity => router.CompleteValidation(message, validity), () => Interlocked.Add(ref _pendingBytes, -size));
        try
        {
            MessageValidity validity;
            try
            {
                validity = validator.Validate(message, verdict);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A local fault is not the sender's: no penalty, and the router caches no id for a later copy.
                if (logger.IsError) logger.Error($"Gossip validation on {message.Topic} from {source} failed", e);
                validity = MessageValidity.Throttled;
            }

            if (!verdict.IsHandedOff)
            {
                verdict.Complete(validity);
            }

            await verdict.Completion.WaitAsync(timeout, stopping);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            verdict.Abandon();
        }
    }
}
