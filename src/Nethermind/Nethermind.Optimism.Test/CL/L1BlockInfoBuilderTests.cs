// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class L1BlockInfoBuilderTests
{
    [Test]
    public void MainnetSystemTx()
    {
        byte[] data = Convert.FromHexString(
            "440a5e200000146b000f79c5000000000000000100000000678f36fb00000000014aac0100000000000000000000000000000000000000000000000000000001763888f90000000000000000000000000000000000000000000000000000000006ec6da8e3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd800000000000000000000000006887246668a3b87f54deb3b94ba47a6f63f32985");
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(data, []);
        l1BlockInfo.BlockHash.Should().Be(new Hash256("0xe3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd80"));
        l1BlockInfo.Number.Should().Be(21670913);
        l1BlockInfo.BaseFee.Should().Be(6278383865);

        var parameters = new CLChainSpecEngineParameters { OptimismPortalProxy = TestItem.AddressA };
        var depositTransactionBuilder = new DepositTransactionBuilder(1, parameters);
        Transaction tx = depositTransactionBuilder.BuildL1InfoTransaction(l1BlockInfo);

        tx.Data!.Value.ToArray().Should().BeEquivalentTo(data);
        tx.SourceHash.Should().Be(new("0x0a17e1f9443295ceee6678f1fc70aa6e45d2988cfbe062ec28db9f1e5fcb469d"));
    }
}
