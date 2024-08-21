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
    public readonly IShutterConfig Cfg;
    public readonly IShutterBlockHandler BlockHandler;
    public readonly IShutterKeyValidator KeyValidator;
    public readonly ShutterTxLoader TxLoader;
    public readonly ShutterTime Time;
    public readonly ShutterEon Eon;
    public ShutterTxSource TxSource { get; }
    public ShutterP2P? P2P;
    public ShutterBlockImprovementContextFactory? BlockImprovementContextFactory;

    private readonly IReadOnlyBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;
    private readonly TimeSpan _slotLength = GnosisSpecProvider.SlotLength;
    private readonly TimeSpan _blockUpToDateCutoff = GnosisSpecProvider.SlotLength;
    private readonly TimeSpan _blockWaitCutoff = TimeSpan.FromMilliseconds(1333);

    public ShutterApi(
        IAbiEncoder abiEncoder,
        IReadOnlyBlockTree readOnlyBlockTree,
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
        _blockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;

        ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = new(worldStateManager, readOnlyBlockTree, specProvider, logManager);

        Time = new(specProvider, timestamper, _slotLength, _blockUpToDateCutoff);
        TxLoader = new(logFinder, cfg, Time, specProvider, ecdsa, logManager);
        Eon = new(readOnlyBlockTree, readOnlyTxProcessingEnvFactory, abiEncoder, cfg, logManager);
        BlockHandler = new ShutterBlockHandler(
            specProvider.ChainId,
            cfg.ValidatorRegistryContractAddress!,
            cfg.ValidatorRegistryMessageVersion,
            readOnlyTxProcessingEnvFactory,
            readOnlyBlockTree,
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
    }

    public void StartP2P(CancellationTokenSource? cancellationTokenSource = null)
        => P2P!.Start(cancellationTokenSource);

    public virtual void NewHeadBlockHandler(object? sender, BlockEventArgs e)
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
            _specProvider,
            _logManager
        );
        return BlockImprovementContextFactory;
    }

    public async ValueTask DisposeAsync()
    {
        await (P2P?.DisposeAsync() ?? default);
    }


    protected void KeysReceivedHandler(object? sender, DecryptionKeys keys)
    {
        KeyValidator.OnDecryptionKeysReceived(keys);
    }

    protected async void KeysValidatedHandler(object? sender, IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        // wait for latest block before loading transactions
        Block? head = (await BlockHandler.WaitForBlockInSlot(keys.Slot - 1, _slotLength, _blockWaitCutoff, new())) ?? _blockTree.Head;
        BlockHeader? header = head?.Header;
        BlockHeader parentHeader = header is not null
            ? _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None)!
            : _blockTree.FindLatestHeader()!;
        TxSource.LoadTransactions(head, parentHeader, keys);
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
}