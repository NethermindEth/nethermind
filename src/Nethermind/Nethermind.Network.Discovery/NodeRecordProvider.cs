// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using System.Net;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery;

public sealed class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IBlockTree blockTree,
    IForkInfo forkInfo,
    ITimestamper timestamper,
    ILogManager logManager
) : INodeRecordProvider
{
    private readonly Lock _lock = new();
    private readonly NodeRecordSigner _enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
    private readonly ILogger _logger = logManager.GetClassLogger<NodeRecordProvider>();
    private Task<LocalNodeRecord>? _nodeRecordTask;
    private bool _subscribed;

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Task<LocalNodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            lock (_lock)
            {
                if (!_subscribed)
                {
                    blockTree.NewHeadBlock += OnNewHeadBlock;
                    _subscribed = true;
                }

                // Build once, guarding concurrent callers (Ping/HandleEnrRequest run from concurrent
                // discovery handlers). Use CancellationToken.None so the cached ENR isn't faulted by a
                // single caller's token; per-call cancellation is honored via WaitAsync below.
                task = _nodeRecordTask ??= PrepareNodeRecord(GetEffectiveHeader(null), previousSequence: 0, CancellationToken.None);
            }
        }

        return (await task.WaitAsync(cancellationToken)).Record;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Task<LocalNodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            return;
        }

        lock (_lock)
        {
            task = _nodeRecordTask;
            if (task is not null)
            {
                _nodeRecordTask = RefreshNodeRecord(task, e.Block.Header);
            }
        }
    }

    private async Task<LocalNodeRecord> RefreshNodeRecord(Task<LocalNodeRecord> currentTask, BlockHeader head)
    {
        LocalNodeRecord current = await currentTask;
        try
        {
            LocalNodeRecordState state = await CreateState(head, CancellationToken.None);
            if (current.State == state)
            {
                return current;
            }

            return CreateSignedRecord(state, NextSequence(current.Record.EnrSequence));
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to refresh local ENR. {e}");
            return current;
        }
    }

    private async Task<LocalNodeRecord> PrepareNodeRecord(BlockHeader? effectiveHeader, ulong previousSequence, CancellationToken cancellationToken)
    {
        LocalNodeRecordState state = await CreateState(effectiveHeader, cancellationToken);
        return CreateSignedRecord(state, NextSequence(previousSequence));
    }

    private async ValueTask<LocalNodeRecordState> CreateState(BlockHeader? effectiveHeader, CancellationToken cancellationToken)
    {
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        BlockHeader? header = GetEffectiveHeader(effectiveHeader);
        NetworkForkId currentForkId = forkInfo.GetForkId(header?.Number ?? 0, header?.Timestamp ?? 0);

        return new LocalNodeRecordState(ip.ExternalIp, networkConfig.P2PPort, networkConfig.DiscoveryPort, currentForkId);
    }

    private BlockHeader? GetEffectiveHeader(BlockHeader? preferredHeader) => preferredHeader ?? blockTree.Head?.Header ?? blockTree.Genesis;

    private LocalNodeRecord CreateSignedRecord(LocalNodeRecordState state, ulong sequence)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(new EthEntry(state.ForkId.HashBytes, state.ForkId.Next));
        selfNodeRecord.SetEntry(new IpEntry(state.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(state.TcpPort));
        selfNodeRecord.SetEntry(new UdpEntry(state.UdpPort));
        selfNodeRecord.SetEntry(new SecP256k1Entry(nodeKey.CompressedPublicKey));
        selfNodeRecord.EnrSequence = sequence;
        _enrSigner.Sign(selfNodeRecord);
        if (!_enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return new LocalNodeRecord(selfNodeRecord, state);
    }

    private ulong NextSequence(ulong previous)
    {
        ulong now = timestamper.UnixTime.Milliseconds;
        return now > previous ? now : previous + 1;
    }

    private sealed record LocalNodeRecord(NodeRecord Record, LocalNodeRecordState State);

    private readonly record struct LocalNodeRecordState(IPAddress ExternalIp, int TcpPort, int UdpPort, NetworkForkId ForkId);
}
