// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class ReadOnlyMintedRecordContractTests
{
    [Test]
    public void UpdateAccounting_ShouldBeNoOp()
    {
        ReadOnlyMintedRecordContract contract = new();

        Assert.DoesNotThrow(() => contract.UpdateAccounting(
            Substitute.For<ITransactionProcessor>(),
            Build.A.XdcBlockHeader().TestObject,
            Substitute.For<IXdcReleaseSpec>(),
            (UInt256)2100,
            UInt256.Zero));
    }
}
