// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Shutter.Test;

public class ShutterApiSimulator(
    IAbiEncoder abiEncoder,
    IReadOnlyBlockTree blockTree,
    IEthereumEcdsa ecdsa,
    ILogFinder logFinder,
    IReceiptStorage receiptStorage,
    ILogManager logManager,
    ISpecProvider specProvider,
    ITimestamper timestamper,
    IWorldStateManager worldStateManager,
    IShutterConfig cfg,
    Dictionary<ulong, byte[]> validatorsInfo,
    Random rnd
        ) : ShutterApi(abiEncoder, blockTree, ecdsa, logFinder, receiptStorage,
        logManager, specProvider, timestamper, worldStateManager, cfg, validatorsInfo, ShutterTestsCommon.SlotLength)
{
    public event EventHandler? EonUpdate;
    private event EventHandler<IShutterKeyValidator.ValidatedKeyArgs>? KeysValidated;
    private event EventHandler<Dto.DecryptionKeys>? KeysReceived;
    private readonly Random _rnd = rnd;
    private readonly IReceiptStorage _receiptStorage = receiptStorage;
    private ShutterEventSimulator? _eventSimulator;

    public void SetEventSimulator(ShutterEventSimulator eventSimulator)
    {
        _eventSimulator = eventSimulator;
    }

    public (List<ShutterEventSimulator.Event> events, Dto.DecryptionKeys keys) AdvanceSlot(int eventCount, int? keyCount = null)
    {
        (List<ShutterEventSimulator.Event> events, Dto.DecryptionKeys keys) x = _eventSimulator!.AdvanceSlot(eventCount, keyCount);
        LogEntry[] logs = x.events.Select(e => e.LogEntry).ToArray();
        InsertShutterReceipts(_blockTree.Head ?? Build.A.Block.TestObject, logs);
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

    public void NextEon()
        => _eventSimulator!.NextEon();

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
        TxLoader.LoadFromReceipts(block, receipts, _eventSimulator!.GetCurrentEonInfo().Eon);
    }

    // fake out P2P module
    protected override void InitP2P(IShutterConfig cfg, ILogManager logManager)
    {
        KeysReceived += KeysReceivedHandler;
    }

    // fake out key validator
    // protected override void RegisterOnKeysValidated()
    // {

    // }

    protected override IShutterEon InitEon()
    {
        IShutterEon eon = Substitute.For<IShutterEon>();
        eon.GetCurrentEonInfo().Returns(_ => _eventSimulator!.GetCurrentEonInfo());
        eon.When(x => x.Update(Arg.Any<BlockHeader>())).Do((_) => EonUpdate?.Invoke(this, new()));
        return eon;
    }

    // set genesis unix timestamp to 1
    protected override ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
    {
        return new(1000, timestamper, _slotLength, _blockUpToDateCutoff);
    }
}
