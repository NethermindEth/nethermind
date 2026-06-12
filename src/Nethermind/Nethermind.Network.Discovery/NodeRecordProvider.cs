// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery;

public sealed class NodeRecordProvider : INodeRecordProvider, IDisposable
{
    private readonly IIPResolver _ipResolver;
    private readonly INetworkConfig _networkConfig;
    private readonly IBlockTree _blockTree;
    private readonly IForkInfo _forkInfo;
    private readonly CompressedPublicKey _publicKey;
    private readonly PrivateKey _privateKey;
    private readonly NodeRecordSigner _enrSigner;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    private NodeRecord? _nodeRecord;
    private NetworkForkId? _currentForkId;
    private long? _currentForkIdHeaderNumber;
    private ulong? _currentForkIdHeaderTimestamp;
    private BlockHeader? _currentSyncPivotHeader;
    private long? _currentSyncPivotNumber;
    private Hash256? _currentSyncPivotHash;
    private IPAddress? _currentExternalIp;
    private bool _disposed;

    public NodeRecordProvider(
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IIPResolver ipResolver,
        IEthereumEcdsa ethereumEcdsa,
        INetworkConfig networkConfig,
        IBlockTree blockTree,
        IForkInfo forkInfo,
        ITimestamper timestamper,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(nodeKey);
        ArgumentNullException.ThrowIfNull(ipResolver);
        ArgumentNullException.ThrowIfNull(ethereumEcdsa);
        ArgumentNullException.ThrowIfNull(networkConfig);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(forkInfo);
        ArgumentNullException.ThrowIfNull(timestamper);
        ArgumentNullException.ThrowIfNull(logManager);

        _ipResolver = ipResolver;
        _networkConfig = networkConfig;
        _blockTree = blockTree;
        _forkInfo = forkInfo;
        _publicKey = nodeKey.CompressedPublicKey;
        _privateKey = nodeKey.Unprotect();
        _enrSigner = new NodeRecordSigner(ethereumEcdsa, _privateKey);
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<NodeRecordProvider>();

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        using (Lock.Scope _ = _lock.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        IIPResolver.NethermindIp ip = await _ipResolver.Resolve(cancellationToken);

        using Lock.Scope __ = _lock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetCurrentNodeRecord(ip.ExternalIp);
    }

    public void Dispose()
    {
        using Lock.Scope _ = _lock.EnterScope();
        if (_disposed)
        {
            return;
        }

        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _privateKey.Dispose();
        _disposed = true;
    }

    private NodeRecord PrepareNodeRecord(NetworkForkId? forkId, ulong enrSequence, IPAddress externalIp)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(externalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        if (forkId is { } currentForkId)
        {
            selfNodeRecord.SetEntry(new EthEntry(currentForkId.HashBytes, currentForkId.Next));
        }
        selfNodeRecord.SetEntry(new SecP256k1Entry(_publicKey));
        selfNodeRecord.EnrSequence = enrSequence;
        SignAndVerify(selfNodeRecord);

        return selfNodeRecord;
    }

    private BlockHeader? GetCurrentHeader()
    {
        Block? head = _blockTree.Head;
        if (head is null)
        {
            return _blockTree.BestSuggestedHeader ?? _blockTree.Genesis;
        }

        BlockHeader headHeader = head.Header;
        if (_blockTree.Genesis is { } genesis && head.Hash == genesis.Hash)
        {
            (long blockNumber, Hash256 blockHash) = _blockTree.SyncPivot;
            const BlockTreeLookupOptions lookupOptions =
                BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
            if (_currentSyncPivotHeader is not null &&
                _currentSyncPivotNumber == blockNumber &&
                _currentSyncPivotHash == blockHash)
            {
                return _currentSyncPivotHeader;
            }

            BlockHeader? syncPivotHeader = _blockTree.FindHeader(blockHash, lookupOptions, blockNumber);
            if (syncPivotHeader is not null)
            {
                _currentSyncPivotHeader = syncPivotHeader;
                _currentSyncPivotNumber = blockNumber;
                _currentSyncPivotHash = blockHash;
                return syncPivotHeader;
            }

            return genesis;
        }

        return headHeader;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        try
        {
            using Lock.Scope _ = _lock.EnterScope();
            if (_disposed || _currentExternalIp is not { } externalIp)
            {
                return;
            }

            UpdateNodeRecord(e.Block.Header, externalIp);
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to update self ENR forkid: {exception}");
        }
    }

    private NodeRecord GetCurrentNodeRecord(IPAddress externalIp) => UpdateNodeRecord(GetCurrentHeader(), externalIp);

    private NodeRecord UpdateNodeRecord(BlockHeader? header, IPAddress externalIp)
    {
        bool externalIpChanged = _currentExternalIp is null || !_currentExternalIp.Equals(externalIp);
        if (header is null)
        {
            if (_nodeRecord is null || externalIpChanged)
            {
                ulong previousSequence = _nodeRecord?.EnrSequence ?? 0;
                _nodeRecord = PrepareNodeRecord(null, GetNextEnrSequence(previousSequence), externalIp);
                _currentForkId = null;
                _currentForkIdHeaderNumber = null;
                _currentForkIdHeaderTimestamp = null;
                _currentExternalIp = externalIp;
            }

            return _nodeRecord;
        }

        if (_nodeRecord is not null &&
            !externalIpChanged &&
            _currentForkIdHeaderNumber == header.Number &&
            _currentForkIdHeaderTimestamp == header.Timestamp)
        {
            return _nodeRecord;
        }

        NetworkForkId forkId = _forkInfo.GetForkId(header.Number, header.Timestamp);
        if (_nodeRecord is null || externalIpChanged || !_currentForkId.HasValue || !_currentForkId.Value.Equals(forkId))
        {
            ulong previousSequence = _nodeRecord?.EnrSequence ?? 0;
            _nodeRecord = PrepareNodeRecord(forkId, GetNextEnrSequence(previousSequence), externalIp);
            _currentForkId = forkId;
            _currentExternalIp = externalIp;
        }

        _currentForkIdHeaderNumber = header.Number;
        _currentForkIdHeaderTimestamp = header.Timestamp;
        return _nodeRecord;
    }

    private ulong GetNextEnrSequence(ulong previousSequence = 0)
    {
        long unixMilliseconds = _timestamper.UtcNowOffset.ToUnixTimeMilliseconds();
        ulong timestampSequence = unixMilliseconds > 0 ? (ulong)unixMilliseconds : 1;
        return timestampSequence > previousSequence ? timestampSequence : checked(previousSequence + 1);
    }

    private void SignAndVerify(NodeRecord nodeRecord)
    {
        _enrSigner.Sign(nodeRecord);
        if (!_enrSigner.Verify(nodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }
    }
}
