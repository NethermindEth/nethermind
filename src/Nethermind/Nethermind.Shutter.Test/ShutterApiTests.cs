// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
using NUnit.Framework;

namespace Nethermind.Shutter.Test;


[TestFixture]
public class ShutterApiTests : ShutterApi
{
    public ShutterApiTests(
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
        ) : base(abiEncoder, readOnlyBlockTree, ecdsa, logFinder, receiptFinder,
            logManager, specProvider, timestamper, worldStateManager, cfg, validatorsInfo)
    { }

    public override void NewHeadBlockHandler(object? sender, BlockEventArgs e)
    { }

    protected override void InitP2P(IShutterConfig cfg, ILogManager logManager)
    { }

    protected override void RegisterOnKeysValidated()
    { }
}
