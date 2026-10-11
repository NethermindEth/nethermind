// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>A connection that can take part in broadcast sessions.</summary>
internal interface ILeanBroadcastPeer
{
    string Description { get; }

    /// <summary>The peer's advertised receive ceiling, which a sender must respect.</summary>
    ulong MaxObjectBytes { get; }

    /// <summary>Whether the peer subscribed to <paramref name="channel"/> through BCAST.</summary>
    bool IsSubscribed(string channel);

    /// <summary>Opens this node's SESS stream toward the peer; writes happen in the background, in order.</summary>
    ILeanBroadcastSession OpenSession(string channel, string messageId, byte[] preamble, byte[] initialUpdate);

    /// <summary>Sends one shard on its own CHUNK stream after the session's SESS Open; false if it could not be written.</summary>
    ValueTask<bool> SendShardAsync(ILeanBroadcastSession session, int index, ReadOnlyMemory<byte> shard);

    /// <summary>Terminates the binding for data attributable to this peer.</summary>
    void Penalize(string reason);
}

/// <summary>This node's outbound SESS stream of one session toward one peer.</summary>
internal interface ILeanBroadcastSession
{
    string Channel { get; }
    string MessageId { get; }

    /// <summary>Sends this node's shard inventory as a routing update.</summary>
    void Update(byte[] bitmap);

    /// <summary>Signals reconstruction with <c>RESET_STREAM(0x01)</c>, asking the peer to stop sending shards.</summary>
    void Complete();

    /// <summary>Ends the stream when the session ends.</summary>
    void Close();
}

/// <summary>How an incoming SESS Open or CHUNK header was handled.</summary>
internal enum LeanBroadcastVerdict
{
    Accepted,

    /// <summary>Refused without fault: not subscribed, unresolved context, timing, duplicate, conflict or budget.</summary>
    Refused,

    /// <summary>Data attributable to the sending peer, which is penalized.</summary>
    Invalid
}

/// <summary>EIP-8437 authenticated broadcast of kind-2 and kind-3 objects over the ethp2p broadcast framework.</summary>
/// <remarks>
/// <para>
/// A session is keyed by its manifest ID. Before a session is created or forwarded, its channel, manifest ID, bounds,
/// signature, duty and slot are checked, and at most one manifest is accepted per <c>(kind, slot, duty_index)</c>. Relays
/// forward only shards whose width and SHA-256 match the manifest, before reconstruction or proof verification. After 16
/// distinct shards the body is reconstructed, its padding, SHA-256 and all 32 shard hashes are checked by re-encoding, and it
/// then goes through the shared transport's canonical object checks and kind validation; nothing is admitted or served
/// through retrieval before that.
/// </para>
/// <para>
/// Faults: a wrong shard hash or a malformed or unauthorized preamble is the supplying peer's; shards that do not form the
/// committed codeword, or an invalid object, are the manifest signer's and penalize no relay. A node already holding the
/// object forwards its shards only if the manifest commits them.
/// </para>
/// </remarks>
internal sealed class LeanBroadcastEngine : IDisposable
{
    /// <summary>Concurrent sessions: two slots of one block-proof duty and a 16-member inclusion-list committee, with margin.</summary>
    internal const int MaxSessions = 64;
    internal const int MaxShardsInFlightPerPeer = 4;

    /// <summary>Successful sends per shard for a relay; origins are unlimited (RS strategy <c>ForwardMultiplier</c>).</summary>
    internal const int ForwardMultiplier = 4;

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private readonly LeanObjectTransport _transport;
    private readonly ILeanBroadcastProfile _profile;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ValueHash256, Session> _sessions = [];
    private readonly Dictionary<string, Session> _byMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<(byte Kind, ulong Slot, ulong Duty), ValueHash256> _duties = [];
    private readonly HashSet<ILeanBroadcastPeer> _peers = [];
    private readonly ITimer _timer;
    private readonly ulong _seed = (ulong)Random.Shared.NextInt64();
    private string[]? _channels;
    private long _shardsAccepted;
    private bool _disposed;

    public LeanBroadcastEngine(LeanObjectTransport transport, ILeanBroadcastProfile profile, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _profile = profile;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<LeanBroadcastEngine>();
        _timer = _clock.CreateTimer(static state => ((LeanBroadcastEngine)state!).Expire(), this, Tick, Tick);
    }

    internal int SessionCount { get { lock (_gate) return _sessions.Count; } }

    /// <summary>Checked shards taken into sessions, for diagnostics.</summary>
    internal long ShardsAccepted => Interlocked.Read(ref _shardsAccepted);

    /// <summary>The channels this node subscribes to: kinds 2 and 3 under the local proof profile and the broadcast profile.</summary>
    public IReadOnlyList<string> Channels
    {
        get
        {
            if (_channels is not null || _transport.BroadcastScope is not { } scope) return _channels ?? [];
            string[] channels = new string[2];
            channels[0] = LeanBroadcastManifest.Channel(scope.ChainId, scope.Genesis, _profile.Id, LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile);
            channels[1] = LeanBroadcastManifest.Channel(scope.ChainId, scope.Genesis, _profile.Id, LeanProtocol.KindInclusionList, LeanObjectTransport.LocalProfile);
            return _channels = channels;
        }
    }

    private bool IsLocalChannel(string channel) => Array.IndexOf((string[])Channels, channel) >= 0;

    /// <summary>Registers a peer whose handshake and Status were both accepted.</summary>
    public void AddPeer(ILeanBroadcastPeer peer)
    {
        lock (_gate) if (!_disposed) _peers.Add(peer);
    }

    public void RemovePeer(ILeanBroadcastPeer peer)
    {
        lock (_gate)
        {
            _peers.Remove(peer);
            foreach (Session session in _sessions.Values) session.Participants.Remove(peer);
        }
    }

    /// <summary>Enrolls a newly subscribed peer in active sessions of the channel.</summary>
    public void OnSubscribed(ILeanBroadcastPeer peer, string channel)
    {
        Outgoing outgoing = new();
        lock (_gate)
        {
            if (!_peers.Contains(peer)) return;
            foreach (Session session in _sessions.Values)
                if (session.Channel == channel && !session.Failed)
                {
                    Enroll(session, peer);
                    Dispatch(session, outgoing);
                }
        }
        outgoing.Run(this);
    }

    /// <summary>Handles a peer's SESS Open, creating or joining the session only once every check has passed.</summary>
    public LeanBroadcastVerdict OnSessionOpen(ILeanBroadcastPeer peer, LeanSessOpen open, out string? reason)
    {
        if (!IsLocalChannel(open.Channel))
        {
            reason = "channel is not subscribed";
            return LeanBroadcastVerdict.Refused;
        }
        if (_transport.BroadcastScope is not { } scope)
        {
            reason = "genesis unknown";
            return LeanBroadcastVerdict.Refused;
        }
        LeanBroadcastManifest manifest;
        byte[] signature, authorization;
        try
        {
            manifest = LeanBroadcastManifest.DecodePreamble(open.Preamble, out signature, out authorization);
        }
        catch (RlpException exception)
        {
            reason = $"malformed preamble: {exception.Message}";
            return LeanBroadcastVerdict.Invalid;
        }
        ValueHash256 manifestId = manifest.Id(scope.ChainId, scope.Genesis);
        if (open.MessageId != LeanBroadcastManifest.MessageId(manifestId))
        {
            reason = "message_id is not the manifest ID";
            return LeanBroadcastVerdict.Invalid;
        }
        if (open.Channel != manifest.Channel(scope.ChainId, scope.Genesis))
        {
            reason = "manifest belongs to another channel";
            return LeanBroadcastVerdict.Invalid;
        }
        if (!TryParseUpdate(open.InitialUpdate, out uint inventory))
        {
            reason = "initial update is not a 4-byte bitmap";
            return LeanBroadcastVerdict.Invalid;
        }

        Outgoing outgoing = new();
        LeanBroadcastVerdict verdict;
        lock (_gate)
        {
            if (_sessions.TryGetValue(manifestId, out Session? existing))
            {
                // Identical manifests share state; a repeat earns no new allowance or expiry.
                Participant participant = Enroll(existing, peer);
                participant.Inventory |= inventory;
                Dispatch(existing, outgoing);
                verdict = LeanBroadcastVerdict.Accepted;
            }
            else verdict = LeanBroadcastVerdict.Refused;
        }
        if (verdict == LeanBroadcastVerdict.Accepted)
        {
            outgoing.Run(this);
            reason = null;
            return verdict;
        }

        verdict = Admit(manifest, manifestId, signature, authorization, out reason);
        if (verdict != LeanBroadcastVerdict.Accepted) return verdict;
        Session? created = Create(manifest, manifestId, open.Channel, open.Preamble, origin: false, out reason);
        if (created is null) return LeanBroadcastVerdict.Refused;
        lock (_gate)
        {
            Participant participant = Enroll(created, peer);
            participant.Inventory |= inventory;
            EnrollSubscribers(created);
            Dispatch(created, outgoing);
        }
        outgoing.Run(this);
        return LeanBroadcastVerdict.Accepted;
    }

    /// <summary>Checks bounds, slot window, signature, duty and the object's descriptor before any session state exists.</summary>
    private LeanBroadcastVerdict Admit(LeanBroadcastManifest manifest, in ValueHash256 manifestId, byte[] signature, byte[] authorization, out string? reason)
    {
        if (manifest.BroadcastProfileId != _profile.Id)
        {
            reason = "broadcast profile is not configured";
            return LeanBroadcastVerdict.Refused;
        }
        ulong current = _profile.CurrentSlot;
        if (manifest.Slot + 1 < current || manifest.Slot > current + 1)
        {
            reason = $"slot {manifest.Slot} is not within one slot of {current}";
            return LeanBroadcastVerdict.Refused;
        }
        if (_transport.CheckBroadcastDescriptor(manifest.Descriptor, manifest.Skeleton) is { } descriptorError)
        {
            reason = descriptorError;
            return LeanBroadcastVerdict.Refused;
        }
        switch (_profile.Authorize(manifest, manifestId, signature, authorization))
        {
            case LeanBroadcastAuthorization.Rejected:
                reason = "producer is not authorized";
                return LeanBroadcastVerdict.Invalid;
            case LeanBroadcastAuthorization.Unresolved:
                reason = "consensus context is unresolved";
                return LeanBroadcastVerdict.Refused;
        }
        reason = null;
        return LeanBroadcastVerdict.Accepted;
    }

    /// <summary>Consumes the duty's allowance and creates the session.</summary>
    private Session? Create(LeanBroadcastManifest manifest, in ValueHash256 manifestId, string channel, byte[] preamble, bool origin, out string? reason)
    {
        (byte, ulong, ulong) duty = (manifest.Descriptor.Kind, manifest.Slot, manifest.DutyIndex);
        long codedBytes = 2L * LeanReedSolomon.DataShards * manifest.ShardBytes;
        lock (_gate)
        {
            if (_disposed)
            {
                reason = "stopped";
                return null;
            }
            if (_sessions.TryGetValue(manifestId, out Session? raced))
            {
                reason = null;
                return raced;
            }
            if (_duties.TryGetValue(duty, out ValueHash256 accepted) && accepted != manifestId)
            {
                reason = "another manifest already holds this duty's allowance";
                return null;
            }
            if (_sessions.Count >= MaxSessions)
            {
                reason = "session limit";
                return null;
            }
            LeanBroadcastAdmission admission = _transport.AdmitBroadcast(manifest.Descriptor, codedBytes);
            if (admission is LeanBroadcastAdmission.Rejected or LeanBroadcastAdmission.Busy)
            {
                reason = $"transport {admission}";
                return null;
            }
            // The allowance stays consumed after failure or eviction, until the duty is no longer eligible.
            _duties[duty] = manifestId;
            DateTimeOffset expiry = _profile.SlotStart(manifest.Slot + 2);
            DateTimeOffset assemblyDeadline = _clock.GetUtcNow() + LeanProtocol.MaxAssemblyAge;
            Session session = new(manifest, manifestId, LeanBroadcastManifest.MessageId(manifestId), channel, preamble,
                expiry < assemblyDeadline ? expiry : assemblyDeadline, origin,
                admission == LeanBroadcastAdmission.Started ? codedBytes : 0);
            if (admission == LeanBroadcastAdmission.Held && _transport.HeldBody(manifest.Descriptor.ObjectId) is { } body)
            {
                byte[][] shards = Encode(body);
                if (Commits(manifest, body, shards)) session.Load(shards, reconstructed: true);
                else
                {
                    // The held object is valid, so the manifest misstates it: the signer's fault. None of its shards is forwarded,
                    // since receivers would penalize this relay for any that miss the manifest's hashes.
                    if (_logger.IsInfo) _logger.Info($"lean/1 broadcast {session.MessageId[..16]} failed: manifest does not commit the held object");
                    Fail(session);
                }
            }
            _sessions.Add(manifestId, session);
            _byMessageId.Add(session.MessageId, session);
            reason = null;
            return session;
        }
    }

    /// <summary>Starts an origin session for a fully validated object with an externally signed manifest.</summary>
    /// <remarks>EIP-8437: originators fully validate an object before broadcasting it, so only stored objects qualify.</remarks>
    public bool Originate(LeanBroadcastManifest manifest, byte[] signature, byte[] authorization)
    {
        if (_transport.BroadcastScope is not { } scope || _transport.HeldBody(manifest.Descriptor.ObjectId) is not { } body) return false;
        ValueHash256 manifestId = manifest.Id(scope.ChainId, scope.Genesis);
        byte[][] shards = Encode(body);
        if (!Commits(manifest, body, shards)) return false;
        if (Admit(manifest, manifestId, signature, authorization, out string? reason) != LeanBroadcastVerdict.Accepted)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 broadcast not originated: {reason}");
            return false;
        }
        Session? session = Create(manifest, manifestId, manifest.Channel(scope.ChainId, scope.Genesis), manifest.Preamble(signature, authorization),
            origin: true, out reason);
        if (session is null) return false;
        Outgoing outgoing = new();
        lock (_gate)
        {
            session.Load(shards, reconstructed: true);
            EnrollSubscribers(session);
            Dispatch(session, outgoing);
        }
        outgoing.Run(this);
        return true;
    }

    /// <summary>Merges a peer's routing update into its inventory.</summary>
    public LeanBroadcastVerdict OnRoutingUpdate(ILeanBroadcastPeer peer, string messageId, byte[] data)
    {
        if (!TryParseUpdate(data, out uint inventory)) return LeanBroadcastVerdict.Invalid;
        Outgoing outgoing = new();
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue(messageId, out Session? session) || !session.Participants.TryGetValue(peer, out Participant? participant))
                return LeanBroadcastVerdict.Refused;
            participant.Inventory |= inventory;
            Dispatch(session, outgoing);
        }
        outgoing.Run(this);
        return LeanBroadcastVerdict.Accepted;
    }

    /// <summary>The peer reset its SESS stream: with <paramref name="reconstructed"/> it needs no more shards, otherwise it left.</summary>
    /// <remarks>Either way this node's SESS stream toward the peer is finished, returning the stream allowance it held.</remarks>
    public void OnPeerSessionEnded(ILeanBroadcastPeer peer, string messageId, bool reconstructed)
    {
        ILeanBroadcastSession? finished = null;
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue(messageId, out Session? session) || !session.Participants.TryGetValue(peer, out Participant? participant)) return;
            if (reconstructed) participant.Completed = true;
            else session.Participants.Remove(peer);
            if (!participant.OutFinished) finished = participant.Out;
            participant.OutFinished = true;
        }
        finished?.Close();
    }

    /// <summary>Checks a CHUNK header before any shard byte is read.</summary>
    /// <param name="index">The shard index of an accepted header.</param>
    /// <param name="unknown">The header names no known session yet, which the caller may defer within its budget.</param>
    public LeanBroadcastVerdict ResolveShard(LeanChunkHeader header, out int index, out bool unknown, out string? reason)
    {
        index = 0;
        unknown = false;
        if (!IsLocalChannel(header.Channel))
        {
            reason = "channel is not subscribed";
            return LeanBroadcastVerdict.Refused;
        }
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue(header.MessageId, out Session? session) || session.Channel != header.Channel)
            {
                unknown = true;
                reason = "unknown manifest";
                return LeanBroadcastVerdict.Refused;
            }
            if (!LeanBroadcastWire.TryParseShardId(header.ChunkId, out index))
            {
                reason = "noncanonical shard ID";
                return LeanBroadcastVerdict.Invalid;
            }
            if (header.DataLength != session.Manifest.ShardBytes)
            {
                reason = "data_length is not shard_bytes";
                return LeanBroadcastVerdict.Invalid;
            }
            if (session.Failed || session.Reconstructed || (session.Have & (1u << index)) != 0)
            {
                reason = "shard not needed";
                return LeanBroadcastVerdict.Refused;
            }
        }
        reason = null;
        return LeanBroadcastVerdict.Accepted;
    }

    /// <summary>Takes a complete shard: checks its SHA-256, records it, forwards it and reconstructs at 16 distinct shards.</summary>
    public LeanBroadcastVerdict OnShard(ILeanBroadcastPeer peer, string messageId, int index, byte[] shard)
    {
        Session? session;
        lock (_gate) _byMessageId.TryGetValue(messageId, out session);
        if (session is null) return LeanBroadcastVerdict.Refused;
        // A wrong shard is attributable to the peer that supplied it.
        if (!session.Manifest.IsCommittedShard(index, shard)) return LeanBroadcastVerdict.Invalid;
        Outgoing outgoing = new();
        bool decode = false;
        lock (_gate)
        {
            if (session.Participants.TryGetValue(peer, out Participant? participant)) participant.Inventory |= 1u << index;
            // A shard received twice earns no progress or forwarding allowance.
            if (session.Failed || session.Reconstructed || session.Decoding || !session.TryAdd(index, shard)) return LeanBroadcastVerdict.Accepted;
            Interlocked.Increment(ref _shardsAccepted);
            if (session.Count == LeanReedSolomon.DataShards)
            {
                session.Decoding = true;
                decode = true;
            }
            foreach (Participant other in session.Participants.Values) if (other.Out is not null) outgoing.Update(other.Out, session.Have);
            Dispatch(session, outgoing);
        }
        outgoing.Run(this);
        if (decode) _ = Task.Run(() => Reconstruct(session));
        return LeanBroadcastVerdict.Accepted;
    }

    private void Reconstruct(Session session)
    {
        // The session's shards are frozen while it decodes, so decoding needs no lock.
        long decoding = Stopwatch.GetTimestamp();
        byte[]? body = session.Decode(out byte[][]? codeword, out string? error);
        LeanMetrics.Coding(Stopwatch.GetElapsedTime(decoding));
        if (body is not null && !_profile.MatchesContext(session.Manifest, body)) error = "object does not match its consensus context";
        if (body is not null && error is null)
            error = _transport.AcceptBroadcastBody(session.Manifest.Descriptor, session.Manifest.Skeleton, body, session.Started);
        Outgoing outgoing = new();
        lock (_gate)
        {
            session.Decoding = false;
            if (error is not null)
            {
                // The checked shards are the signer's responsibility: no relay is penalized, and retrieval stays possible.
                if (_logger.IsInfo) _logger.Info($"lean/1 broadcast {session.MessageId[..16]} failed: {error}");
                Fail(session);
            }
            else
            {
                // The object is complete: its codeword, kept to forward shards until the session ends, is bounded by
                // MaxSessions rather than charged to the transport's incomplete-assembly budget.
                if (session.CodedBytes > 0) _transport.ReleaseBroadcast(session.CodedBytes);
                session.CodedBytes = 0;
                session.Load(codeword!, reconstructed: true);
                foreach (Participant participant in session.Participants.Values)
                    if (participant.Out is not null) outgoing.Complete(participant.Out);
                Dispatch(session, outgoing);
            }
        }
        outgoing.Run(this);
    }

    private void Fail(Session session)
    {
        session.Failed = true;
        session.Release();
        if (session.CodedBytes > 0) _transport.ReleaseBroadcast(session.CodedBytes);
        session.CodedBytes = 0;
    }

    private Participant Enroll(Session session, ILeanBroadcastPeer peer)
    {
        if (!session.Participants.TryGetValue(peer, out Participant? participant))
            session.Participants[peer] = participant = new Participant(peer);
        // Each side opens one SESS stream toward the other for a session, once the peer subscribed to its channel.
        if (participant.Out is null && !participant.OutFinished && !session.Failed && peer.IsSubscribed(session.Channel)
            && session.Manifest.Descriptor.ByteLength <= peer.MaxObjectBytes)
        {
            participant.Out = peer.OpenSession(session.Channel, session.MessageId, session.Preamble, LeanBroadcastWire.Bitmap(session.Have));
            if (session.Reconstructed && !session.Origin) participant.Out.Complete();
        }
        return participant;
    }

    private void EnrollSubscribers(Session session)
    {
        foreach (ILeanBroadcastPeer peer in _peers)
            if (peer.IsSubscribed(session.Channel)) Enroll(session, peer);
    }

    /// <summary>Plans shard sends: least-sent shards first, to peers whose inventory lacks them, within relay budgets.</summary>
    private void Dispatch(Session session, Outgoing outgoing)
    {
        if (session.Failed || session.Have == 0) return;
        foreach (Participant participant in session.Participants.Values)
        {
            if (participant.Completed || participant.Out is null) continue;
            while (BitOperations.PopCount(participant.InFlight) < MaxShardsInFlightPerPeer)
            {
                uint wanted = session.Have & ~participant.Inventory & ~participant.InFlight;
                int best = -1;
                for (uint bits = wanted; bits != 0; bits &= bits - 1)
                {
                    int index = BitOperations.TrailingZeroCount(bits);
                    if (!session.Origin && session.Sent[index] >= ForwardMultiplier) continue;
                    if (best < 0 || session.Sent[index] < session.Sent[best]
                        || (session.Sent[index] == session.Sent[best] && Priority(index) < Priority(best))) best = index;
                }
                if (best < 0) break;
                participant.InFlight |= 1u << best;
                session.Sent[best]++;
                outgoing.Send(session, participant, best, session.Shard(best));
            }
        }
    }

    /// <summary>Per-relay Fibonacci-hash tie break, so neighbouring relays favour different shards.</summary>
    private uint Priority(int index) => (uint)((_seed ^ (ulong)index * 0x9E3779B97F4A7C15UL) >> 32);

    private async Task SendAsync(Session session, Participant participant, int index, byte[] shard)
    {
        bool sent = await participant.Peer.SendShardAsync(participant.Out!, index, shard).ConfigureAwait(false);
        Outgoing outgoing = new();
        lock (_gate)
        {
            participant.InFlight &= ~(1u << index);
            if (sent) participant.Inventory |= 1u << index;
            else session.Sent[index]--;
            if (sent) Dispatch(session, outgoing);
        }
        outgoing.Run(this);
    }

    /// <summary>Ends sessions at the start of <c>slot + 2</c> or their assembly deadline and prunes ineligible duties.</summary>
    private void Expire()
    {
        List<ILeanBroadcastSession>? closed = null;
        lock (_gate)
        {
            if (_disposed) return;
            DateTimeOffset now = _clock.GetUtcNow();
            List<Session>? expired = null;
            foreach (Session session in _sessions.Values) if (session.Expiry <= now) (expired ??= []).Add(session);
            if (expired is not null)
                foreach (Session session in expired)
                {
                    _sessions.Remove(session.ManifestId);
                    _byMessageId.Remove(session.MessageId);
                    foreach (Participant participant in session.Participants.Values) if (participant.Out is not null) (closed ??= []).Add(participant.Out);
                    if (!session.Failed) Fail(session);
                }
            ulong current = _profile.CurrentSlot;
            List<(byte, ulong, ulong)>? stale = null;
            foreach ((byte, ulong Slot, ulong) duty in _duties.Keys) if (duty.Slot + 2 < current) (stale ??= []).Add(duty);
            if (stale is not null) foreach ((byte, ulong, ulong) duty in stale) _duties.Remove(duty);
        }
        if (closed is not null) foreach (ILeanBroadcastSession stream in closed) stream.Close();
    }

    /// <summary>Whether the manifest commits <paramref name="body"/> and its codeword <paramref name="shards"/>.</summary>
    private static bool Commits(LeanBroadcastManifest manifest, byte[] body, byte[][] shards)
    {
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(manifest.BodySha256.Bytes)) return false;
        for (int i = 0; i < shards.Length; i++) if (!manifest.IsCommittedShard(i, shards[i])) return false;
        return true;
    }

    private static byte[][] Encode(byte[] body)
    {
        long started = Stopwatch.GetTimestamp();
        byte[][] shards = LeanReedSolomon.Encode(body);
        LeanMetrics.Coding(Stopwatch.GetElapsedTime(started));
        return shards;
    }

    private static bool TryParseUpdate(byte[] data, out uint inventory)
    {
        inventory = 0;
        // An absent initial update advertises no shards.
        return data.Length == 0 || LeanBroadcastWire.TryParseBitmap(data, out inventory);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Session session in _sessions.Values) if (!session.Failed) Fail(session);
            _sessions.Clear();
            _byMessageId.Clear();
        }
        _timer.Dispose();
    }

    private sealed class Participant(ILeanBroadcastPeer peer)
    {
        public ILeanBroadcastPeer Peer { get; } = peer;
        public uint Inventory { get; set; }
        public uint InFlight { get; set; }
        public bool Completed { get; set; }
        public ILeanBroadcastSession? Out { get; set; }

        /// <summary>Whether this node finished its SESS stream toward the peer, which is never reopened for the session.</summary>
        public bool OutFinished { get; set; }
    }

    private sealed class Session(LeanBroadcastManifest manifest, ValueHash256 manifestId, string messageId, string channel, byte[] preamble,
        DateTimeOffset expiry, bool origin, long codedBytes)
    {
        private byte[]?[] _shards = new byte[LeanReedSolomon.TotalShards][];

        public LeanBroadcastManifest Manifest { get; } = manifest;
        public ValueHash256 ManifestId { get; } = manifestId;
        public string MessageId { get; } = messageId;
        public string Channel { get; } = channel;
        public byte[] Preamble { get; } = preamble;
        public DateTimeOffset Expiry { get; } = expiry;
        public bool Origin { get; } = origin;

        /// <summary>The <see cref="Stopwatch"/> timestamp at which the session was accepted.</summary>
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long CodedBytes { get; set; } = codedBytes;
        public Dictionary<ILeanBroadcastPeer, Participant> Participants { get; } = [];
        public int[] Sent { get; } = new int[LeanReedSolomon.TotalShards];
        public uint Have { get; private set; }
        public int Count => BitOperations.PopCount(Have);
        public bool Decoding { get; set; }
        public bool Reconstructed { get; private set; }
        public bool Failed { get; set; }

        public byte[] Shard(int index) => _shards[index]!;

        public bool TryAdd(int index, byte[] shard)
        {
            if ((Have & (1u << index)) != 0) return false;
            _shards[index] = shard;
            Have |= 1u << index;
            return true;
        }

        public void Load(byte[][] shards, bool reconstructed)
        {
            _shards = shards;
            Have = uint.MaxValue;
            Reconstructed = reconstructed;
        }

        public byte[]? Decode(out byte[][]? codeword, out string? error)
        {
            int[] indices = new int[LeanReedSolomon.DataShards];
            byte[][] shards = new byte[LeanReedSolomon.DataShards][];
            int found = 0;
            for (int i = 0; i < LeanReedSolomon.TotalShards && found < indices.Length; i++)
                if (_shards[i] is { } shard)
                {
                    indices[found] = i;
                    shards[found++] = shard;
                }
            return Manifest.Reconstruct(indices, shards, out codeword, out error);
        }

        public void Release()
        {
            _shards = new byte[LeanReedSolomon.TotalShards][];
            Have = 0;
        }
    }

    /// <summary>Stream writes collected under the lock and started after it is released.</summary>
    private sealed class Outgoing
    {
        private List<(Session, Participant, int, byte[])>? _sends;
        private List<(ILeanBroadcastSession, byte[])>? _updates;
        private List<ILeanBroadcastSession>? _completions;

        public void Send(Session session, Participant participant, int index, byte[] shard) => (_sends ??= []).Add((session, participant, index, shard));
        public void Update(ILeanBroadcastSession stream, uint have) => (_updates ??= []).Add((stream, LeanBroadcastWire.Bitmap(have)));
        public void Complete(ILeanBroadcastSession stream) => (_completions ??= []).Add(stream);

        public void Run(LeanBroadcastEngine engine)
        {
            if (_updates is not null) foreach ((ILeanBroadcastSession stream, byte[] bitmap) in _updates) stream.Update(bitmap);
            if (_completions is not null) foreach (ILeanBroadcastSession stream in _completions) stream.Complete();
            if (_sends is not null)
                foreach ((Session session, Participant participant, int index, byte[] shard) in _sends)
                    _ = engine.SendAsync(session, participant, index, shard);
        }
    }
}
