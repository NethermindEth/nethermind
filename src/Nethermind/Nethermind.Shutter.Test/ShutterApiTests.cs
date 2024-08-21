// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterApiTests : ShutterApi
{
    public ShutterApiTests(
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
        ) : base(abiEncoder, blockTree, ecdsa, logFinder, receiptFinder,
            logManager, specProvider, timestamper, worldStateManager, cfg, validatorsInfo)
    { }

    public event EventHandler? EonUpdate;
    private event EventHandler<IShutterKeyValidator.ValidatedKeyArgs>? KeysValidated;
    private event EventHandler<Dto.DecryptionKeys>? KeysReceived;
    private event EventHandler<BlockEventArgs>? NewHeadBlock;
    private IShutterEon.Info? _currentEonInfo;

    public void TriggerKeysValidated(IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        KeysValidated?.Invoke(this, keys);
    }

    public void TriggerKeysReceived(Dto.DecryptionKeys keys)
    {
        KeysReceived?.Invoke(this, keys);
    }

    public void TriggerNewHeadBlock(BlockEventArgs e)
    {
        NewHeadBlock?.Invoke(this, e);
    }

    public void SetEon(IShutterEon.Info eonInfo)
    {
        _currentEonInfo = eonInfo;
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

    // fakeout blocktree event
    protected override void RegisterNewHeadBlock()
    {
        NewHeadBlock += NewHeadBlockHandler;
    }

    protected override IShutterEon InitEon()
    {
        IShutterEon eon = Substitute.For<IShutterEon>();
        eon.GetCurrentEonInfo().Returns(_currentEonInfo);
        eon.When(x => x.Update(Arg.Any<BlockHeader>())).Do((_) => EonUpdate?.Invoke(this, new()));
        return eon;
    }
}
