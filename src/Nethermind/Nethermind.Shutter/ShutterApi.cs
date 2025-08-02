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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade.Find;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Shutter.Config;

namespace Nethermind.Shutter;

public class ShutterApi : IShutterApi
{
    public virtual TimeSpan BlockWaitCutoff { get => _blockWaitCutoff; }

    public readonly ShutterBlockHandler BlockHandler;
    public readonly IShutterKeyValidator KeyValidator;
    public readonly IShutterEon Eon;
    public readonly ShutterTxLoader TxLoader;
    public readonly SlotTime Time;
    public ShutterTxSource TxSource { get; }
    public IShutterP2P? P2P;
    public ShutterBlockImprovementContextFactory? BlockImprovementContextFactory;

    protected readonly TimeSpan _slotLength;
    protected readonly TimeSpan _blockUpToDateCutoff;
    protected readonly IReadOnlyBlockTree _readOnlyBlockTree;
    protected readonly IBlockTree _blockTree;
    private readonly IShareableTxProcessorSource _txProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogManager _logManager;
    private readonly IFileSystem _fileSystem;
    private readonly IKeyStoreConfig _keyStoreConfig;
    private readonly IShutterConfig _cfg;
    private readonly TimeSpan _blockWaitCutoff;

    public ShutterApi(
        IAbiEncoder abiEncoder,
        IBlockTree blockTree,
        IEthereumEcdsa ecdsa,
        ILogFinder logFinder,
        IReceiptFinder receiptFinder,
        ILogManager logManager,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IShareableTxProcessorSource txProcessorSource,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        IProcessExitSource processExitSource,
        IFileSystem fileSystem,
        IBlockProcessingQueue blockProcessingQueue,
        IKeyStoreConfig keyStoreConfig,
        IShutterConfig shutterConfig,
        IBlocksConfig blocksConfig,
        IIPResolver ipResolver)
    {
        _cfg = shutterConfig;
        _blockTree = blockTree;
        _readOnlyBlockTree = blockTree.AsReadOnly();
        _abiEncoder = abiEncoder;
        _logManager = logManager;
        _slotLength = TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot);
        _fileSystem = fileSystem;
        _keyStoreConfig = keyStoreConfig;
        _blockUpToDateCutoff = TimeSpan.FromMilliseconds(_cfg.BlockUpToDateCutoff);
        _blockWaitCutoff = _slotLength / 3;
        _txProcessorSource = txProcessorSource;

        ShutterValidatorsInfo validatorsInfo = new();
        if (shutterConfig!.ValidatorInfoFile is not null)
        {
            try
            {
                validatorsInfo.Load(shutterConfig!.ValidatorInfoFile);
            }
            catch (Exception e)
            {
                throw new ShutterPlugin.ShutterLoadingException("Could not load Shutter validator info file", e);
            }
        }

        Time = InitTime(specProvider, timestamper);
        TxLoader = new(logFinder, _cfg, Time, specProvider, ecdsa, abiEncoder, logManager);
        Eon = InitEon();
        BlockHandler = new ShutterBlockHandler(
            blockTree,
            receiptFinder,
            Eon,
            TxLoader,
            Time,
            logManager,
            _slotLength,
            BlockWaitCutoff);

        _ = new ShutterKeyRegistrationChecker(
            validatorsInfo,
            specProvider.ChainId,
            _cfg,
            blockTree,
            blockProcessingQueue,
            backgroundTaskScheduler,
            processExitSource,
            _txProcessorSource,
            abiEncoder,
            logManager
        );

        TxSource = new ShutterTxSource(TxLoader, _cfg, Time, logManager);

        KeyValidator = new ShutterKeyValidator(_cfg, Eon, logManager);

        InitP2P(ipResolver.ExternalIp);
    }

    public Task StartP2P(IEnumerable<Multiaddress> bootnodeP2PAddresses, CancellationToken cancellationToken)
        => P2P!.Start(bootnodeP2PAddresses, OnKeysReceived, cancellationToken);

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
        => new ShutterEon(_readOnlyBlockTree, _txProcessorSource, _abiEncoder, _cfg, _logManager);

    protected virtual SlotTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
        => new(specProvider.BeaconChainGenesisTimestamp!.Value * 1000, timestamper, _slotLength, _blockUpToDateCutoff);
}
