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
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.State;

namespace Nethermind.Shutter;

public class ShutterApi : IShutterApi
{
    public virtual TimeSpan BlockWaitCutoff { get => _blockWaitCutoff; }

    public readonly IShutterBlockHandler BlockHandler;
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
        IWorldStateManager worldStateManager,
        IShutterConfig cfg,
        Dictionary<ulong, byte[]> validatorsInfo,
        TimeSpan slotLength
        )
    {
        _cfg = cfg;
        _blockTree = blockTree;
        _readOnlyBlockTree = blockTree.AsReadOnly();
        _abiEncoder = abiEncoder;
        _logManager = logManager;
        _slotLength = slotLength;
        _blockUpToDateCutoff = slotLength;
        _blockWaitCutoff = _slotLength / 3;

        _txProcessingEnvFactory = new(worldStateManager, blockTree, specProvider, logManager);

        Time = InitTime(specProvider, timestamper);
        TxLoader = new(logFinder, _cfg, Time, specProvider, ecdsa, abiEncoder, logManager);
        Eon = InitEon();
        BlockHandler = new ShutterBlockHandler(
            specProvider.ChainId,
            _cfg,
            _txProcessingEnvFactory,
            blockTree,
            abiEncoder,
            receiptFinder,
            validatorsInfo,
            Eon,
            TxLoader,
            Time,
            logManager,
            _slotLength,
            BlockWaitCutoff);

        TxSource = new ShutterTxSource(TxLoader, _cfg, Time, logManager);

        KeyValidator = new ShutterKeyValidator(_cfg, Eon, logManager);

        InitP2P(_cfg, logManager);
    }

    public Task StartP2P(CancellationTokenSource? cancellationTokenSource = null)
        => P2P!.Start(cancellationTokenSource);

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

    protected virtual async void OnKeysReceived(object? sender, IShutterP2P.KeysReceivedArgs keysReceivedArgs)
    {
        IShutterKeyValidator.ValidatedKeys? keys = KeyValidator.ValidateKeys(keysReceivedArgs.Keys);

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

    protected virtual void InitP2P(IShutterConfig cfg, ILogManager logManager)
    {
        P2P = new ShutterP2P(cfg, logManager);
        P2P.KeysReceived += OnKeysReceived;
    }

    protected virtual IShutterEon InitEon()
        => new ShutterEon(_readOnlyBlockTree, _txProcessingEnvFactory, _abiEncoder, _cfg, _logManager);

    protected virtual ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
    {
        return new(specProvider.BeaconChainGenesisTimestamp!.Value * 1000, timestamper, _slotLength, _blockUpToDateCutoff);
    }
}
