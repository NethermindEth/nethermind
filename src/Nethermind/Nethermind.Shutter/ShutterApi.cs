// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Shutter.Dto;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Shutter;

public class ShutterApi : IShutterApi
{

    public static readonly TimeSpan SlotLength = GnosisSpecProvider.SlotLength;
    public static readonly TimeSpan BlockUpToDateCutoff = GnosisSpecProvider.SlotLength;
    public virtual TimeSpan BlockWaitCutoff { get => TimeSpan.FromMilliseconds(1333); }

    public readonly IShutterConfig Cfg;
    public readonly IShutterBlockHandler BlockHandler;
    public readonly IShutterKeyValidator KeyValidator;
    public readonly IShutterEon Eon;
    public readonly ShutterTxLoader TxLoader;
    public readonly ShutterTime Time;
    public ShutterTxSource TxSource { get; }
    public ShutterP2P? P2P;
    public ShutterBlockImprovementContextFactory? BlockImprovementContextFactory;

    protected readonly IReadOnlyBlockTree _blockTree;
    private readonly ReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory;
    private readonly ISpecProvider _specProvider;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogManager _logManager;

    public ShutterApi(
        IAbiEncoder abiEncoder,
        IReadOnlyBlockTree blockTree,
        IEthereumEcdsa ecdsa,
        ILogFinder logFinder,
        IReceiptFinder receiptFinder,
        ILogManager logManager,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IWorldStateManager worldStateManager,
        IShutterConfig cfg,
        Dictionary<ulong, byte[]> validatorsInfo
        )
    {
        Cfg = cfg;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _abiEncoder = abiEncoder;
        _logManager = logManager;

        _txProcessingEnvFactory = new(worldStateManager, blockTree, specProvider, logManager);

        Time = InitTime(specProvider, timestamper);
        TxLoader = new(logFinder, cfg, Time, specProvider, ecdsa, logManager);
        Eon = InitEon();
        BlockHandler = new ShutterBlockHandler(
            specProvider.ChainId,
            cfg.ValidatorRegistryContractAddress!,
            cfg.ValidatorRegistryMessageVersion,
            _txProcessingEnvFactory,
            blockTree,
            abiEncoder,
            receiptFinder,
            validatorsInfo,
            Eon,
            TxLoader,
            Time,
            logManager);

        TxSource = new ShutterTxSource(TxLoader, cfg, Time, logManager);

        KeyValidator = new ShutterKeyValidator(cfg, Eon, logManager);

        InitP2P(cfg, logManager);
        RegisterOnKeysValidated();
        RegisterNewHeadBlock();
    }

    public void StartP2P(CancellationTokenSource? cancellationTokenSource = null)
        => P2P!.Start(cancellationTokenSource);

    protected virtual void NewHeadBlockHandler(object? sender, BlockEventArgs e)
    {
        BlockHandler.OnNewHeadBlock(e.Block);
    }

    public ShutterBlockImprovementContextFactory GetBlockImprovementContextFactory(IBlockProducer blockProducer)
    {
        BlockImprovementContextFactory ??= new(
            blockProducer,
            TxSource,
            Cfg,
            Time,
            _logManager
        );
        return BlockImprovementContextFactory;
    }

    public async ValueTask DisposeAsync()
    {
        TxSource.Dispose();
        BlockHandler.Dispose();
        await (P2P?.DisposeAsync() ?? default);
    }

    protected void KeysReceivedHandler(object? sender, DecryptionKeys keys)
    {
        KeyValidator.OnDecryptionKeysReceived(keys);
    }

    protected virtual async void KeysValidatedHandler(object? sender, IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        // wait for latest block before loading transactions
        Block? head = (await BlockHandler.WaitForBlockInSlot(keys.Slot - 1, SlotLength, BlockWaitCutoff, new())) ?? _blockTree.Head;
        BlockHeader? header = head?.Header;
        BlockHeader parentHeader = header is not null
            ? _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None)!
            : _blockTree.FindLatestHeader()!;
        TxSource.LoadTransactions(head, parentHeader, keys);
    }

    protected virtual void RegisterNewHeadBlock()
    {
        _blockTree.NewHeadBlock += NewHeadBlockHandler;
    }

    protected virtual void InitP2P(IShutterConfig cfg, ILogManager logManager)
    {
        P2P = new(cfg, logManager);
        P2P.KeysReceived += KeysReceivedHandler;
    }

    protected virtual void RegisterOnKeysValidated()
    {
        KeyValidator.KeysValidated += KeysValidatedHandler;
    }

    protected virtual IShutterEon InitEon()
        => new ShutterEon(_blockTree, _txProcessingEnvFactory, _abiEncoder, Cfg, _logManager);

    protected virtual ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
    {
        ulong genesisTimestamp = specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return new(genesisTimestamp, timestamper, SlotLength, BlockUpToDateCutoff);
    }
}
