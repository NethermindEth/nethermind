// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Find;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Shutter.Test;

public class ShutterApiSimulator(
    ShutterEventSimulator eventSimulator,
    IAbiEncoder abiEncoder,
    IBlockTree blockTree,
    IEthereumEcdsa ecdsa,
    ILogFinder logFinder,
    IReceiptStorage receiptStorage,
    ILogManager logManager,
    ISpecProvider specProvider,
    ITimestamper timestamper,
    IWorldStateManager worldStateManager,
    IFileSystem fileSystem,
    IKeyStoreConfig keyStoreConfig,
    IShutterConfig cfg,
    Random rnd
        ) : ShutterApi(abiEncoder, blockTree, ecdsa, logFinder, receiptStorage,
{
    private readonly Random _rnd = rnd;
    private readonly IReceiptStorage _receiptStorage = receiptStorage;
    private ShutterEventSimulator? _eventSimulator;

    public void SetEventSimulator(ShutterEventSimulator eventSimulator)
    {
        _eventSimulator = eventSimulator;
    }

    public (List<ShutterEventSimulator.Event> events, Dto.DecryptionKeys keys) AdvanceSlot(int eventCount, int? keyCount = null)
    {
        LogEntry[] logs = x.events.Select(e => e.LogEntry).ToArray();
        TriggerKeysReceived(x.keys);
        return x;
    }

    public void TriggerKeysValidated(IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        KeysValidated?.Invoke(this, keys);
    }

    public void TriggerKeysReceived(Dto.DecryptionKeys keys)
    {
        KeysReceived?.Invoke(this, keys);
    }

    public void TriggerNewHeadBlock(BlockEventArgs e)

    public void TriggerKeysReceived(Dto.DecryptionKeys keys)
        => _ = OnKeysReceived(keys);

    public void NextEon()

    public void InsertShutterReceipts(Block block, in LogEntry[] logs)
    {
        var receipts = new TxReceipt[logs.Length];
        block.Header.Bloom = new(logs);

        // one log per receipt
        for (int i = 0; i < logs.Length; i++)
        {
            var h = new byte[32];
            _rnd.NextBytes(h);
            receipts[i] = Build.A.Receipt
                .WithLogs([logs[i]])
                .WithTransactionHash(new(h))
                .WithBlockHash(block.Hash)
                .WithBlockNumber(block.Number)
                .TestObject;
        }

        _receiptStorage.Insert(block, receipts);
    }

        {
    }

    // fake out key validator
    // protected override void RegisterOnKeysValidated()
    // {


    protected override IShutterEon InitEon()
    {
        IShutterEon eon = Substitute.For<IShutterEon>();
        return eon;
    }

    // set genesis unix timestamp to 1
    protected override ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
    {
        return new(1000, timestamper, _slotLength, _blockUpToDateCutoff);
    }
}
