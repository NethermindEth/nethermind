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
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade.Find;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.State;

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
    private readonly IReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory;
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
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IFileSystem fileSystem,
        IKeyStoreConfig keyStoreConfig,
        IShutterConfig cfg,
        ShutterValidatorsInfo validatorsInfo,
        TimeSpan slotLength,
        IPAddress ip
        )
    {
        _cfg = cfg;
        _blockTree = blockTree;
        _readOnlyBlockTree = blockTree.AsReadOnly();
        _abiEncoder = abiEncoder;
        _logManager = logManager;
        _slotLength = slotLength;
        _fileSystem = fileSystem;
        _keyStoreConfig = keyStoreConfig;
        _blockUpToDateCutoff = TimeSpan.FromMilliseconds(cfg.BlockUpToDateCutoff);
        _blockWaitCutoff = _slotLength / 3;

        _txProcessingEnvFactory = txProcessingEnvFactory;

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

        InitP2P(ip);
    }

    public Task StartP2P(IEnumerable<Multiaddress> bootnodeP2PAddresses, CancellationTokenSource? cancellationTokenSource = null)
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

    protected virtual SlotTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
        => new(specProvider.BeaconChainGenesisTimestamp!.Value * 1000, timestamper, _slotLength, _blockUpToDateCutoff);
}
