// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

[TestFixture]
public class DepositTransactionBuilderTest
{
    private static readonly Address DepositAddress = TestItem.AddressA;
    private static readonly Address SomeAddressA = TestItem.AddressB;
    private static readonly Address SomeAddressB = TestItem.AddressC;

    private readonly DepositTransactionBuilder _builder;

    public DepositTransactionBuilderTest()
    {
        var parameters = new CLChainSpecEngineParameters();
        _builder = new DepositTransactionBuilder(TestBlockchainIds.ChainId, parameters);
    }

    [Test]
    public void DeriveUserDeposits_NoDeposits()
    {
        List<OptimismTxReceipt> receipts = [];
        List<Transaction> depositTransactions = _builder.BuildUserDepositTransactions(DepositAddress, receipts);

        depositTransactions.Count.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_OtherLog()
    {
        var blockHash = TestItem.KeccakA;

        List<OptimismTxReceipt> receipts =
        [
            new(Build.A.Receipt
                .WithTxType(TxType.EIP1559)
                .WithState(Hash256.Zero)
                .WithStatusCode(1)
                .WithBloom(Bloom.Empty)
                .WithLogs(
                    Build.A.LogEntry
                        .WithAddress(SomeAddressA)
                        .TestObject
                )
                .WithTransactionHash(Hash256.Zero)
                .WithContractAddress(Address.Zero)
                .WithBlockHash(blockHash)
                .TestObject)
        ];
        List<Transaction> depositTransactions = _builder.BuildUserDepositTransactions(DepositAddress, receipts);

        depositTransactions.Count.Should().Be(0);
    }
}
