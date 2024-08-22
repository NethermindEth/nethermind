
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;

using static Nethermind.Merge.Plugin.Test.EngineModuleTests;

namespace Nethermind.Shutter.Test;
class ShutterTestsCommon
{
    public const int Seed = 100;
    public const ulong InitialSlot = 16082024;
    public const ulong InitialTxPointer = 1000;
    public const int ChainId = BlockchainIds.Chiado;
    public const ulong GenesisTimestamp = ChiadoSpecProvider.BeaconChainGenesisTimestamp;
    public static readonly ISpecProvider SpecProvider = ChiadoSpecProvider.Instance;
    public static readonly IEthereumEcdsa Ecdsa = new EthereumEcdsa(ChainId);
    public static readonly ILogManager LogManager = LimboLogs.Instance;
    public static readonly AbiEncoder AbiEncoder = new();
    public static readonly ShutterConfig Cfg = new()
    {
        InstanceID = 0,
        ValidatorRegistryContractAddress = Address.Zero.ToString(),
        ValidatorRegistryMessageVersion = 0,
        KeyBroadcastContractAddress = Address.Zero.ToString(),
        KeyperSetManagerContractAddress = Address.Zero.ToString(),
        SequencerContractAddress = Address.Zero.ToString(),
        EncryptedGasLimit = 21000 * 20
    };

    public static ShutterApiSimulator InitApi(Random rnd, ITimestamper? timestamper = null)
    {
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IReadOnlyBlockTree readOnlyBlockTree = Substitute.For<IReadOnlyBlockTree>();
        ILogFinder logFinder = Substitute.For<ILogFinder>();
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        return new(
            AbiEncoder, readOnlyBlockTree, Ecdsa, logFinder, receiptStorage,
            LogManager, SpecProvider, timestamper ?? Substitute.For<ITimestamper>(),
            worldStateManager, Cfg, [], rnd
        );
    }

    public static ShutterApiSimulator InitApi(Random rnd, MergeTestBlockchain chain)
        => new(
            AbiEncoder, chain.BlockTree.AsReadOnly(), chain.EthereumEcdsa, chain.LogFinder, chain.ReceiptStorage,
            chain.LogManager, chain.SpecProvider, chain.Timestamper, chain.WorldStateManager, Cfg, [], rnd
        );

    public static ShutterEventSimulator InitEventSimulator(Random rnd, ulong eon, ulong threshhold, ulong initialTxPointer, AbiEncodingInfo abi)
        => new(
            rnd,
            ChainId,
            eon,
            threshhold,
            InitialSlot,
            initialTxPointer,
            AbiEncoder,
            new(Cfg.SequencerContractAddress!),
            abi
        );

    public static Timestamper InitTimestamper(ulong slotTimestamp, ulong offsetMs)
    {
        ulong timestampMs = slotTimestamp * 1000 + offsetMs;
        var blockTime = DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs);
        return new(blockTime.UtcDateTime);
    }
}
