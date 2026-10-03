// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Eip8288;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Streams bounded wrapper chunks while preserving the shared proof-admission path.</summary>
public sealed class Lean2ProtocolHandler : LeanProtocolHandler, IStaticProtocolInfo
{
    public new static byte Version => 2;
    public override string Name => "lean2";
    public override byte ProtocolVersion => Version;
    private readonly LeanChunkReassembler _reassembler;
    private readonly int _chunkSize;

    public Lean2ProtocolHandler(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler, ILogManager logManager, IBlockTree blockTree,
        ProofWrapperService wrappers, LeanProofGossip gossip, LeanReassemblyBudget budget,
        int chunkSize = LeanProofChunkMessage.DefaultChunkSize)
        : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager, blockTree, wrappers, gossip)
    {
        if (chunkSize is not (16 * 1024 or 32 * 1024 or LeanProofChunkMessage.DefaultChunkSize or LeanProofChunkMessage.MaxChunkSize))
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _chunkSize = chunkSize;
        _reassembler = new(budget, onIncompleteAbuse: () =>
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Repeated incomplete lean streams"));
    }

    public override void Init()
    {
        if (Session.HasAgreedCapability(new Capability(Code, Version))
            && (Session is not ILeanBulkSession bulk || !bulk.EnableLeanBulk()))
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean streaming transport unavailable");
            Dispose();
            return;
        }
        base.Init();
    }

    protected override void DropIncompleteForPressure() => _reassembler.DropIncompleteForPressure();

    protected override LeanProofWrapperMessage? DecodeWrapper(ZeroPacket message)
        => _reassembler.Add(Deserialize<LeanProofChunkMessage>(message.Content));

    protected override async ValueTask<bool> BroadcastAsync(ReadOnlyMemory<byte> wrapper, ValueHash256 hash, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(StopToken, cancellationToken);
        if (!CanBroadcast || Session is not ILeanBulkSession bulk) return false;
        int count = (wrapper.Length + _chunkSize - 1) / _chunkSize;
        for (int index = 0; index < count; index++)
        {
            if (!CanBroadcast) return false;
            int offset = index * _chunkSize;
            LeanProofChunkMessage chunk = new(hash, wrapper.Length, index, count, _chunkSize,
                wrapper.Slice(offset, Math.Min(_chunkSize, wrapper.Length - offset)));
            if (await bulk.DeliverLeanChunkAsync(chunk, linked.Token).ConfigureAwait(false) == 0) return false;
        }
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        _reassembler.Dispose();
    }
}
