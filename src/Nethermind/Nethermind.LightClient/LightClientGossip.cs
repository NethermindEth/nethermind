// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.LightClient.Consensus;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;

namespace Nethermind.LightClient;

/// <summary>Validates and forwards light-client gossip only after local consensus authentication.</summary>
internal sealed class LightClientGossip(BeaconChainSpec spec, ILogManager logs, TimeProvider? time = null) : IAsyncDisposable
{
    internal const int MaxUpdateBytes = 16 * 1024;
    internal const int MaxPendingUpdates = 32;
    private const ulong SyncMessageDueBps = 3_333;
    private const ulong SyncMessageDueBpsGloas = 2_500;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Nethermind.Logging.ILogger _logger = logs.GetClassLogger<LightClientGossip>();
    private readonly SemaphoreSlim _validation = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lifetime = new();
    private readonly Dictionary<string, ITopic> _subscriptions = new(StringComparer.Ordinal);
    private HashSet<string> _activeTopics = new(StringComparer.Ordinal);
    private Func<LightClientFinalityUpdate, CancellationToken, Task<bool>>? _finality;
    private Func<LightClientOptimisticUpdate, CancellationToken, Task<bool>>? _optimistic;
    private PubsubRouter? _router;
    private Task? _rotation;
    private CancellationTokenRegistration _shutdown;
    private TaskCompletionSource? _drained;
    private int _handlers;
    private bool _disposed;
    private ulong? _forwardedFinalitySlot;
    private bool _forwardedSupermajority;
    private byte[]? _forwardedFinalityFields;
    private ulong? _forwardedOptimisticSlot;
    private ulong? _subscriptionEpoch;
    internal Task Completion => _rotation ?? Task.CompletedTask;

    internal void Configure(PubsubRouter router, PubsubSettings settings)
    {
        _router = router;
        settings.MaxPendingValidationMessages = MaxPendingUpdates;
        settings.MaxPendingValidationBytes = MaxPendingUpdates * Eth2MessageId.MaxCompressedLength(MaxUpdateBytes);
        settings.MaxRpcBytes = 1024 * 1024;
        settings.MaxIwantResponseBytes = settings.MaxRpcBytes;
        router.VerifyMessage = Verify;
        router.OnDeferredMessage = ValidateDeferredAsync;
    }

    internal void Start(Func<string, ITopic> getTopic,
        Func<LightClientFinalityUpdate, CancellationToken, Task<bool>> finality,
        Func<LightClientOptimisticUpdate, CancellationToken, Task<bool>> optimistic,
        CancellationToken token)
    {
        _finality = finality;
        _optimistic = optimistic;
        _shutdown = token.Register(static state => ((CancellationTokenSource)state!).Cancel(), _stop);
        RefreshTopics(getTopic);
        _rotation = RotateAsync(getTopic);
    }

    internal void RefreshTopics(Func<string, ITopic> getTopic)
    {
        ulong epoch = spec.GetEpoch(CurrentSlot());
        if (_subscriptionEpoch == epoch) return;
        HashSet<string> active = new(StringComparer.Ordinal);
        foreach ((byte[] digest, _) in GossipTopics.DigestsAround(spec, epoch))
        {
            active.Add(GossipTopics.Topic(digest, GossipTopics.LightClientFinalityUpdate));
            active.Add(GossipTopics.Topic(digest, GossipTopics.LightClientOptimisticUpdate));
        }
        Volatile.Write(ref _activeTopics, active);
        foreach (string name in active)
        {
            if (_subscriptions.ContainsKey(name)) continue;
            ITopic topic = getTopic(name);
            topic.Subscribe();
            _subscriptions.Add(name, topic);
            if (_logger.IsInfo) _logger.Info($"Subscribed light-client gossip {name}");
        }
        List<string> existing = [.. _subscriptions.Keys];
        foreach (string retired in existing)
        {
            if (active.Contains(retired)) continue;
            _subscriptions[retired].Unsubscribe();
            _subscriptions.Remove(retired);
        }
        _subscriptionEpoch = epoch;
    }

    private async Task RotateAsync(Func<string, ITopic> getTopic)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(spec.SecondsPerSlot), _time);
            while (await timer.WaitForNextTickAsync(_stop.Token)) RefreshTopics(getTopic);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal MessageValidity Verify(PeerId source, Message message)
    {
        if (_stop.IsCancellationRequested || _finality is null) return MessageValidity.Throttled;
        if (!GossipTopics.TryParse(message.Topic, out _, out string? name) || !GossipTopics.IsEth2TopicName(name!))
            return MessageValidity.Rejected;
        if (name is not (GossipTopics.LightClientFinalityUpdate or GossipTopics.LightClientOptimisticUpdate))
            return MessageValidity.Ignored;
        if (!Volatile.Read(ref _activeTopics).Contains(message.Topic)) return MessageValidity.Throttled;
        return message.Data.Length <= Eth2MessageId.MaxCompressedLength(MaxUpdateBytes)
            ? MessageValidity.Deferred : MessageValidity.Rejected;
    }

    private async Task ValidateDeferredAsync(PeerId source, Message message)
    {
        lock (_lifetime)
        {
            if (_disposed)
            {
                _router!.CompleteValidation(message, MessageValidity.Throttled);
                return;
            }
            _handlers++;
        }
        try
        {
            using CancellationTokenSource deadline = new(ValidationTimeout, _time);
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token);
            await HandleAsync(message.Topic, message.Data.Memory,
                validity => _router!.CompleteValidation(message, validity), attempt.Token);
        }
        catch (OperationCanceledException)
        {
            _router!.CompleteValidation(message, MessageValidity.Throttled);
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Light-client gossip processing from {source} failed", exception);
            _router!.CompleteValidation(message, MessageValidity.Throttled);
        }
        finally
        {
            lock (_lifetime)
            {
                if (--_handlers == 0) _drained?.TrySetResult();
            }
        }
    }

    internal async Task<MessageValidity> HandleAsync(string topic, ReadOnlyMemory<byte> compressed,
        Func<MessageValidity, bool> complete, CancellationToken token)
    {
        await _validation.WaitAsync(token);
        bool forwardingIgnored = false;
        try
        {
            if (!Volatile.Read(ref _activeTopics).Contains(topic)) return Finish(MessageValidity.Throttled);
            if (Eth2MessageId.TryDecompress(compressed.Span, MaxUpdateBytes, out byte[]? ssz) != SnappyDecodeResult.Decoded)
                return Finish(MessageValidity.Rejected);
            GossipTopics.TryParse(topic, out byte[]? digest, out string? name);
            if (name == GossipTopics.LightClientFinalityUpdate)
            {
                LightClientFinalityUpdate update = LightClientWireCodec.DecodeFinality(ssz!, spec);
                if (update.FinalizedHeader?.Beacon is null || update.SyncAggregate?.SyncCommitteeBits is not { Length: Presets.SyncCommitteeSize })
                    return Finish(MessageValidity.Rejected);
                ulong attestedSlot = update.AttestedHeader!.Beacon!.Slot;
                if (!MatchesDigest(digest!, attestedSlot)) return Finish(MessageValidity.Rejected);
                ulong finalizedSlot = update.FinalizedHeader!.Beacon!.Slot;
                int participants = 0;
                foreach (bool bit in update.SyncAggregate!.SyncCommitteeBits!) if (bit) participants++;
                bool supermajority = participants * 3 > Presets.SyncCommitteeSize * 2;
                bool mayForward = IsDue(update.SignatureSlot) &&
                    (_forwardedFinalitySlot is null || finalizedSlot > _forwardedFinalitySlot ||
                     finalizedSlot == _forwardedFinalitySlot && supermajority && !_forwardedSupermajority);
                forwardingIgnored = !mayForward;
                bool advanced = await _finality!(update, token);
                MessageValidity validity = mayForward && advanced ? MessageValidity.Accepted : MessageValidity.Ignored;
                if (complete(validity) && validity == MessageValidity.Accepted)
                {
                    _forwardedFinalitySlot = finalizedSlot;
                    _forwardedSupermajority = supermajority;
                    _forwardedFinalityFields = LightClientWireCodec.EncodeOptimistic(new LightClientOptimisticUpdate
                    {
                        AttestedHeader = update.AttestedHeader,
                        SyncAggregate = update.SyncAggregate,
                        SignatureSlot = update.SignatureSlot,
                    }, spec);
                }
                return validity;
            }
            LightClientOptimisticUpdate optimistic = LightClientWireCodec.DecodeOptimistic(ssz!, spec);
            ulong optimisticSlot = optimistic.AttestedHeader!.Beacon!.Slot;
            if (!MatchesDigest(digest!, optimisticSlot)) return Finish(MessageValidity.Rejected);
            bool eligible = IsDue(optimistic.SignatureSlot) &&
                (_forwardedOptimisticSlot is null || optimisticSlot > _forwardedOptimisticSlot);
            forwardingIgnored = !eligible;
            bool progressed = await _optimistic!(optimistic, token);
            bool matchesFinality = _forwardedFinalityFields is not null && ssz!.AsSpan().SequenceEqual(_forwardedFinalityFields);
            MessageValidity result = eligible && (progressed || matchesFinality) ? MessageValidity.Accepted : MessageValidity.Ignored;
            if (complete(result) && result == MessageValidity.Accepted) _forwardedOptimisticSlot = optimisticSlot;
            return result;
        }
        catch (IrrelevantLightClientUpdateException) { return Finish(MessageValidity.Ignored); }
        catch (LightClientLocalStateException) { return Finish(forwardingIgnored ? MessageValidity.Ignored : MessageValidity.Throttled); }
        catch (InvalidDataException) { return Finish(forwardingIgnored ? MessageValidity.Ignored : MessageValidity.Rejected); }
        finally { _validation.Release(); }

        MessageValidity Finish(MessageValidity validity)
        {
            complete(validity);
            return validity;
        }
    }

    private ulong CurrentSlot() => spec.GetSlotAtTime((ulong)Math.Max(0, _time.GetUtcNow().ToUnixTimeSeconds()));
    private bool MatchesDigest(byte[] digest, ulong slot) => digest.AsSpan().SequenceEqual(ForkDigest.Compute(spec, spec.GetEpoch(slot)));

    internal bool IsDue(ulong signatureSlot)
    {
        ulong slotMs = spec.SecondsPerSlot * 1000;
        ulong basisPoints = spec.GetEpoch(signatureSlot) >= spec.GloasForkEpoch
            ? SyncMessageDueBpsGloas : SyncMessageDueBps;
        ulong delay = slotMs * basisPoints / Presets.BasisPoints;
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (now < 0) return false;
        ulong toleratedNow = (ulong)now + (ulong)GossipRouter.MaximumGossipClockDisparityMs;
        return toleratedNow >= spec.GenesisTime * 1000 &&
            signatureSlot <= (toleratedNow - spec.GenesisTime * 1000) / slotMs &&
            toleratedNow - spec.GenesisTime * 1000 - signatureSlot * slotMs >= delay;
    }

    public async ValueTask DisposeAsync()
    {
        Task handlers;
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            handlers = _handlers == 0 ? Task.CompletedTask : (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await _stop.CancelAsync();
        try
        {
            if (_rotation is not null) await _rotation;
        }
        finally
        {
            Volatile.Write(ref _activeTopics, new HashSet<string>(StringComparer.Ordinal));
            foreach (ITopic topic in _subscriptions.Values) topic.Unsubscribe();
            _subscriptions.Clear();
            await handlers;
            _shutdown.Dispose();
            _validation.Dispose();
            _stop.Dispose();
        }
    }
}
