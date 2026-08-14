// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
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
        Assert.That(l1BlockInfo.BlockHash, Is.EqualTo(new Hash256("0xe3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd80")));
        Assert.That(l1BlockInfo.Number, Is.EqualTo(21670913));
        Assert.That(l1BlockInfo.BaseFee, Is.EqualTo((UInt256)6278383865));

        CLChainSpecEngineParameters parameters = new() { OptimismPortalProxy = TestItem.AddressA };
        DepositTransactionBuilder depositTransactionBuilder = new(1, parameters);
        Transaction tx = depositTransactionBuilder.BuildL1InfoTransaction(l1BlockInfo);

        Assert.That(tx.Data.ToArray(), Is.EqualTo(data));
        Assert.That(tx.SourceHash, Is.EqualTo(new Hash256("0x0a17e1f9443295ceee6678f1fc70aa6e45d2988cfbe062ec28db9f1e5fcb469d")));
    }
}
