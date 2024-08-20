
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Shutter.Test;
class ShutterTestsCommon
{
    public const int Seed = 100;
    public const ulong InitialSlot = 16082024;
    public const ulong InitialTxPointer = 1000;
    public const int ChainId = BlockchainIds.Chiado;
    public static readonly TimeSpan SlotLength = GnosisSpecProvider.SlotLength;
    public static readonly TimeSpan BlockUpToDateCutoff = GnosisSpecProvider.SlotLength;
    public static readonly ISpecProvider SpecProvider = ChiadoSpecProvider.Instance;
    public static readonly IEthereumEcdsa Ecdsa = new EthereumEcdsa(ChainId);
    public static readonly ILogManager LogManager = LimboLogs.Instance;
    public static readonly AbiEncoder AbiEncoder = new();
    public static readonly ShutterConfig Cfg = new()
    {
        ValidatorRegistryContractAddress = Address.Zero.ToString(),
        ValidatorRegistryMessageVersion = 0,
        KeyBroadcastContractAddress = Address.Zero.ToString(),
        KeyperSetManagerContractAddress = Address.Zero.ToString(),
        SequencerContractAddress = Address.Zero.ToString(),
        EncryptedGasLimit = 21000 * 20
    };

    public static readonly ShutterTime Time = new(
        SpecProvider,
        new Timestamper(),
        SlotLength,
        BlockUpToDateCutoff);

    public static ShutterTxLoader InitTxLoader(ILogFinder logFinder)
        => new(
            logFinder,
            Cfg,
            Time,
            ChiadoSpecProvider.Instance,
            Ecdsa,
            LimboLogs.Instance
        );

    public static ShutterEon InitEon()
        => InitEon(
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IReadOnlyBlockTree>()
        );

    public static ShutterEon InitEon(IWorldStateManager worldStateManager, IReadOnlyBlockTree readOnlyBlockTree)
    {
        ReadOnlyTxProcessingEnvFactory txProcessingEnvFactory = new(
            worldStateManager,
            readOnlyBlockTree,
            SpecProvider,
            LogManager
        );

        return new(
            readOnlyBlockTree,
            txProcessingEnvFactory,
            AbiEncoder,
            Cfg,
            LogManager
        );
    }

    public static ShutterMessageHandler InitMessageHandler()
        => new(Cfg, InitEon(), LogManager);

    public static ShutterMessageHandler InitMessageHandler(ShutterEon eon)
        => new(Cfg, eon, LogManager);

    public static ShutterBlockHandler InitBlockHandler()
        => InitBlockHandler(
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IReadOnlyBlockTree>(),
            Substitute.For<ILogFinder>(),
            Substitute.For<IReceiptFinder>()
        );
    public static ShutterBlockHandler InitBlockHandler(IWorldStateManager worldStateManager, IReadOnlyBlockTree readOnlyBlockTree, ILogFinder logFinder, IReceiptFinder receiptFinder)
    {
        ReadOnlyTxProcessingEnvFactory txProcessingEnvFactory = new(
            worldStateManager,
            readOnlyBlockTree,
            SpecProvider,
            LogManager
        );

        return new(
            BlockchainIds.Gnosis,
            Cfg.ValidatorRegistryContractAddress!,
            Cfg.ValidatorRegistryMessageVersion,
            txProcessingEnvFactory,
            readOnlyBlockTree,
            AbiEncoder,
            receiptFinder,
            [],
            InitEon(worldStateManager, readOnlyBlockTree),
            InitTxLoader(logFinder),
            Time,
            LogManager
        );
    }

    public static IEnumerable<ShutterEventEmitter.Event> EmitEvents(Random rnd, ulong eon, ulong initialTxPointer, AbiEncodingInfo abi)
        => new ShutterEventEmitter(
            rnd,
            ChainId,
            eon,
            initialTxPointer,
            AbiEncoder,
            new(Cfg.SequencerContractAddress!),
            abi
        ).EmitEvents();

    public static IEnumerable<ShutterEventEmitter.Event> EmitHalfInvalidEvents(Random rnd, ulong initialTxPointer, AbiEncodingInfo abi)
    {
        ShutterEventEmitter emitter = new(
            rnd,
            ChainId,
            0,
            initialTxPointer,
            AbiEncoder,
            new(Cfg.SequencerContractAddress!),
            abi
        );

        IEnumerable<Transaction> emitHalfInvalid()
        {
            bool valid = false;
            while (true)
            {
                valid = !valid;
                yield return valid
                    ? emitter.DefaultTx
                    : Build.A.Transaction.TestObject;
            }
        }

        return emitter.EmitEvents(emitter.EmitDefaultGasLimits(), emitHalfInvalid());
    }

    public static (LogEntry[], List<(byte[] IdentityPreimage, byte[] Key)>) GetFromEventsSource(IEnumerable<ShutterEventEmitter.Event> eventSource, int count)
    {
        List<ShutterEventEmitter.Event> events = eventSource.Take(count).ToList();
        LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();
        List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
        keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
        return (logs, keys);
    }
}