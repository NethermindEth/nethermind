// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Shutter.Config;

namespace Nethermind.Shutter;

public class ShutterApi : IShutterApi
{
    public virtual TimeSpan BlockWaitCutoff { get => _blockWaitCutoff; }

    public readonly ShutterBlockHandler BlockHandler;
    public readonly IShutterKeyValidator KeyValidator;
    public readonly IShutterEon Eon;
    public readonly ShutterTxLoader TxLoader;
    public readonly ShutterTime Time;
    public ShutterTxSource TxSource { get; }
    public IShutterP2P? P2P;
    public ShutterBlockImprovementContextFactory? BlockImprovementContextFactory;

    protected readonly TimeSpan _slotLength;
    protected readonly TimeSpan _blockUpToDateCutoff;
    protected readonly IReadOnlyBlockTree _readOnlyBlockTree;
    protected readonly IBlockTree _blockTree;
    private readonly ReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogManager _logManager;
    private readonly IFileSystem _fileSystem;
    private readonly IKeyStoreConfig _keyStoreConfig;
    private readonly IShutterConfig _cfg;
    private readonly TimeSpan _blockWaitCutoff;

    public ShutterApi(INethermindApi api, Dictionary<ulong, byte[]> validatorsInfo, TimeSpan slotLength)
    {
        _cfg = api.Config<IShutterConfig>();
        _blockTree = api.BlockTree!;
        _readOnlyBlockTree = _blockTree.AsReadOnly();
        _abiEncoder = api.AbiEncoder;
        _logManager = api.LogManager;
        _fileSystem = api.FileSystem;
        _keyStoreConfig = api.Config<IKeyStoreConfig>();
        _slotLength = slotLength;
        _blockUpToDateCutoff = slotLength;
        _blockWaitCutoff = _slotLength / 3;

        _txProcessingEnvFactory = new(api.WorldStateManager!, _blockTree, api.SpecProvider, _logManager);

        Time = InitTime(api.SpecProvider!, api.Timestamper);
        TxLoader = new(
            api.LogFinder!,
            _cfg,
            Time,
            api.SpecProvider!,
            api.EthereumEcdsa!,
            _abiEncoder,
            _logManager
        );
        Eon = InitEon();
        BlockHandler = new ShutterBlockHandler(
            api.SpecProvider!.ChainId,
            _cfg,
            _txProcessingEnvFactory,
            _blockTree,
            _abiEncoder,
            api.ReceiptFinder!,
            validatorsInfo,
            Eon,
            TxLoader,
            Time,
            _logManager,
            _slotLength,
            BlockWaitCutoff);

        TxSource = new ShutterTxSource(TxLoader, _cfg, Time, _logManager);

        KeyValidator = new ShutterKeyValidator(_cfg, Eon, _logManager);

        InitP2P(api.IpResolver!.ExternalIp);
    }

    public Task StartP2P(Multiaddress[] bootnodeP2PAddresses, CancellationTokenSource? cancellationTokenSource = null)
        => P2P!.Start(bootnodeP2PAddresses, OnKeysReceived, cancellationTokenSource);

    public ShutterBlockImprovementContextFactory GetBlockImprovementContextFactory(IBlockProducer blockProducer)
    {
        BlockImprovementContextFactory ??= new(
            blockProducer,
            TxSource,
            _cfg,
            Time,
            _logManager,
            _slotLength
        );
        return BlockImprovementContextFactory;
    }

    public async ValueTask DisposeAsync()
    {
        TxSource.Dispose();
        BlockHandler.Dispose();
        await (P2P?.DisposeAsync() ?? default);
    }

    protected virtual async Task OnKeysReceived(Dto.DecryptionKeys decryptionKeys)
    {
        IShutterKeyValidator.ValidatedKeys? keys = KeyValidator.ValidateKeys(decryptionKeys);

        if (keys is null)
        {
            return;
        }

        Metrics.ShutterTxPointer = keys.Value.TxPointer;

        // wait for latest block before loading transactions
        Block? head = (await BlockHandler.WaitForBlockInSlot(keys.Value.Slot - 1, new())) ?? _readOnlyBlockTree.Head;
        BlockHeader? header = head?.Header;
        BlockHeader parentHeader = header is not null
            ? _readOnlyBlockTree.FindParentHeader(header, BlockTreeLookupOptions.None)!
            : _readOnlyBlockTree.FindLatestHeader()!;
        TxSource.LoadTransactions(head, parentHeader, keys.Value);
    }

    protected virtual void InitP2P(IPAddress ip)
    {
        P2P = new ShutterP2P(_cfg, _logManager, _fileSystem, _keyStoreConfig, ip);
    }

    protected virtual IShutterEon InitEon()
        => new ShutterEon(_readOnlyBlockTree, _txProcessingEnvFactory, _abiEncoder, _cfg, _logManager);

    protected virtual ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
        => new(specProvider.BeaconChainGenesisTimestamp!.Value * 1000, timestamper, _slotLength, _blockUpToDateCutoff);
}
