// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
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
    ILogManager logManager,
    NetworkListenerState listenerState
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
        TaskCompletionSource<LocalNodeRecord>? initialRecord = null;
        if (task is null)
        {
            lock (_lock)
            {
                if (!_subscribed)
                {
                    blockTree.NewHeadBlock += OnNewHeadBlock;
                    listenerState.Changed += OnListenerChanged;
                    _subscribed = true;
                }

                // Build once, guarding concurrent callers (Ping/HandleEnrRequest run from concurrent
                // discovery handlers). Use CancellationToken.None so the cached ENR isn't faulted by a
                // single caller's token; per-call cancellation is honored via WaitAsync below.
                task = _nodeRecordTask;
                if (task is null)
                {
                    initialRecord = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = _nodeRecordTask = initialRecord.Task;
                }
            }
        }

        if (initialRecord is not null)
        {
            _ = CompleteInitialRecord(initialRecord);
        }

        try
        {
            return (await task.WaitAsync(cancellationToken)).Record;
        }
        catch when (task.IsFaulted)
        {
            _ = Interlocked.CompareExchange(ref _nodeRecordTask, null, task);
            throw;
        }
    }

    private async Task CompleteInitialRecord(TaskCompletionSource<LocalNodeRecord> completion)
    {
        try
        {
            completion.SetResult(await PrepareNodeRecord(GetEffectiveHeader(null), previousSequence: 0, CancellationToken.None));
        }
        catch (Exception e)
        {
            completion.SetException(e);
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        => RefreshRecord(e.Block.Header);

    private void OnListenerChanged(object? sender, EventArgs e)
        => RefreshRecord(effectiveHeader: null);

    private void RefreshRecord(BlockHeader? effectiveHeader)
    {
        lock (_lock)
        {
            if (_nodeRecordTask is { } task)
            {
                _nodeRecordTask = RefreshNodeRecord(task, effectiveHeader);
            }
        }
    }

    private async Task<LocalNodeRecord> RefreshNodeRecord(Task<LocalNodeRecord> currentTask, BlockHeader? effectiveHeader)
    {
        LocalNodeRecord current;
        try
        {
            current = await currentTask;
        }
        catch
        {
            return await PrepareNodeRecord(effectiveHeader, previousSequence: 0, CancellationToken.None);
        }

        try
        {
            IIPResolver.NethermindIp ip = await ipResolver.Resolve(CancellationToken.None);
            LocalNodeRecordState state = CreateState(effectiveHeader, ip);
            if (current.State == state)
            {
                return current;
            }

            if (!Equals(current.State.ExternalIpV4, state.ExternalIpV4) ||
                !Equals(current.State.ExternalIpV6, state.ExternalIpV6))
            {
                LogEndpointIssues(ip, state);
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
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        LocalNodeRecordState state = CreateState(effectiveHeader, ip);
        LogEndpointIssues(ip, state);
        return CreateSignedRecord(state, NextSequence(previousSequence));
    }

    private LocalNodeRecordState CreateState(BlockHeader? effectiveHeader, IIPResolver.NethermindIp ip)
    {
        BlockHeader? header = GetEffectiveHeader(effectiveHeader);
        NetworkForkId currentForkId = forkInfo.GetForkId(header?.Number ?? 0, header?.Timestamp ?? 0);

        IPAddress? rlpxAddress = listenerState.RlpxAddress;
        IPAddress? discoveryAddress = listenerState.DiscoveryAddress;
        bool advertiseV4 = SupportsEveryBoundListener(rlpxAddress, discoveryAddress, AddressFamily.InterNetwork);
        bool advertiseV6 = SupportsEveryBoundListener(rlpxAddress, discoveryAddress, AddressFamily.InterNetworkV6);

        return new LocalNodeRecordState(
            advertiseV4 ? ip.ExternalIpV4 : null,
            rlpxAddress is not null ? networkConfig.P2PPort : null,
            discoveryAddress is not null ? networkConfig.DiscoveryPort : null,
            advertiseV6 ? ip.ExternalIpV6 : null,
            currentForkId);
    }

    private static bool SupportsEveryBoundListener(IPAddress? rlpxAddress, IPAddress? discoveryAddress, AddressFamily family)
        => (rlpxAddress is not null || discoveryAddress is not null) &&
           (rlpxAddress is null || DiscoveryAddressSupport.SupportsFamily(rlpxAddress, family)) &&
           (discoveryAddress is null || DiscoveryAddressSupport.SupportsFamily(discoveryAddress, family));

    private void LogEndpointIssues(IIPResolver.NethermindIp ip, LocalNodeRecordState state)
    {
        if (!_logger.IsWarn)
        {
            return;
        }

        if (ip.ExternalIpV4 is not null && state.ExternalIpV4 is null)
        {
            _logger.Warn("External IPv4 address is available but cannot be advertised for the bound listener combination.");
        }

        if (ip.ExternalIpV6 is not null && state.ExternalIpV6 is null)
        {
            _logger.Warn("External IPv6 address is available but cannot be advertised for the bound listener combination.");
        }

        if (state.ExternalIpV4 is null && state.ExternalIpV6 is null)
        {
            _logger.Warn("No external IP address is advertised; the node will not be discoverable by peers.");
        }
    }

    private BlockHeader? GetEffectiveHeader(BlockHeader? preferredHeader) => preferredHeader ?? blockTree.Head?.Header ?? blockTree.Genesis;

    private LocalNodeRecord CreateSignedRecord(LocalNodeRecordState state, ulong sequence)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(new EthEntry(state.ForkId.HashBytes, state.ForkId.Next));
        if (state.ExternalIpV4 is not null)
        {
            selfNodeRecord.SetEntry(new IpEntry(state.ExternalIpV4));
            if (state.TcpPort is { } tcpPort) selfNodeRecord.SetEntry(new TcpEntry(tcpPort));
            if (state.UdpPort is { } udpPort) selfNodeRecord.SetEntry(new UdpEntry(udpPort));
        }

        if (state.ExternalIpV6 is not null)
        {
            selfNodeRecord.SetEntry(new Ip6Entry(state.ExternalIpV6));
            // Some ENR consumers do not implement EIP-778's fallback from tcp6/udp6 to tcp/udp.
            if (state.TcpPort is { } tcpPort) selfNodeRecord.SetEntry(new Tcp6Entry(tcpPort));
            if (state.UdpPort is { } udpPort) selfNodeRecord.SetEntry(new Udp6Entry(udpPort));
        }
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

    private readonly record struct LocalNodeRecordState(
        IPAddress? ExternalIpV4,
        int? TcpPort,
        int? UdpPort,
        IPAddress? ExternalIpV6,
        NetworkForkId ForkId);
}
