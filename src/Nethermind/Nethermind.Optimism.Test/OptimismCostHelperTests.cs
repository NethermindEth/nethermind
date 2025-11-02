// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismCostHelperTests
{
    [OneTimeSetUp]
    public void Setup()
    {
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());
        TxDecoder.Instance.RegisterDecoder(new OptimismLegacyTxDecoder());
    }

    // https://specs.optimism.io/protocol/jovian/l1-attributes.html#overview
    private const UInt16 DefaultDaFootprintGasScalar = 400;
    private const uint MinTxSize = 100;

    private static IEnumerable<TestCaseData<Transaction[]>> DaFootprintTestCases()
    {
        yield return new(
            []
        )
        { ExpectedResult = 0, TestName = "No txs" };

        yield return new([
                Build.A.Transaction.WithType(TxType.DepositTx).TestObject
            ]
        )
        { ExpectedResult = 0, TestName = "Deposit txs only" };

        yield return new([
                Build.A.Transaction.WithType(TxType.DepositTx).TestObject,
                Build.A.Transaction
                    .To(Build.An.Address.TestObject)
                    .WithNonce(1)
                    .TestObject,
                Build.A.Transaction.WithType(TxType.DepositTx).TestObject
            ]
        )
        { ExpectedResult = MinTxSize * DefaultDaFootprintGasScalar, TestName = "Deposit and legacy txs" };

        yield return new([
                Build.A.Transaction
                    .To(Build.An.Address.TestObject)
                    .WithNonce(1)
                    .WithDataHex("0102030405060708")
                    .TestObject,
                Build.A.Transaction
                    .To(Build.An.Address.TestObject)
                    .WithNonce(2)
                    .WithDataHex("0102030405060708090A0B0C0D0E0F10")
                    .TestObject
            ]
        )
        { ExpectedResult = 2 * MinTxSize * DefaultDaFootprintGasScalar, TestName = "Txs with small data" };

        yield return new([
                Build.A.Transaction
                    .To(Build.An.Address.TestObject)
                    .WithNonce(1)
                    .WithDataHex("0102030405060708")
                    .TestObject,
                Build.A.Transaction
                    .To(Build.An.Address.TestObject)
                    .WithNonce(2)
                    .WithData(new Random(42).NextBytes(512))
                    .TestObject
            ]
        )
        { ExpectedResult = 172000 + MinTxSize * DefaultDaFootprintGasScalar, TestName = "Txs with small and large data" };
    }

    [TestCaseSource(nameof(DaFootprintTestCases))]
    public long ComputeDaFootprint(Transaction[] transactions)
    {
        Address l1BlockAddr = Build.An.Address.TestObject;
        var specHelper = Substitute.For<IOptimismSpecHelper>();
        var worldState = new WorldStateStab(new() {
            // https://specs.optimism.io/protocol/jovian/exec-engine.html#scalar-loading
            { new(l1BlockAddr, new UInt256(8)), DefaultDaFootprintGasScalar.ToBigEndianByteArray().PadLeft(12 + sizeof(UInt16)) }
        });

        var helper = new OptimismCostHelper(specHelper, l1BlockAddr);

        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        return (long)helper.ComputeDaFootprint(block, worldState);
    }

    private class WorldStateStab(Dictionary<StorageCell, byte[]> state) : WorldState(Substitute.For<ITrieStore>(), Substitute.For<IKeyValueStoreWithBatching>(), LimboLogs.Instance), IWorldState
    {
        // ReadOnlySpan return value cannot be mocked
        ReadOnlySpan<byte> IWorldState.Get(in StorageCell storageCell) => state.TryGetValue(storageCell, out var value) ? value : ReadOnlySpan<byte>.Empty;
    }
}
