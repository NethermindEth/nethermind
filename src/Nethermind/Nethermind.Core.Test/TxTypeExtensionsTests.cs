// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TxTypeExtensionsTests
{
    [Test]
    public void TxType_Should_Contain_All_Expected_Values()
    {
        // while adding new txs types, please add a new test case in the below test TxTypes_supported_functionality
        var expectedTxTypes = new[]
        {
            TxType.Legacy,
            TxType.AccessList,
            TxType.EIP1559,
            TxType.Blob,
            TxType.SetCode,
            TxType.DepositTx
        };

        TxType[] actualTxTypes = (TxType[])Enum.GetValues(typeof(TxType));
        Assert.That(actualTxTypes.Length, Is.EqualTo(expectedTxTypes.Length));
        Assert.That(actualTxTypes, Is.EquivalentTo(expectedTxTypes));
    }

    [TestCase(TxType.Legacy, false, false, false, false)]
    [TestCase(TxType.AccessList, true, false, false, false)]
    [TestCase(TxType.EIP1559, true, true, false, false)]
    [TestCase(TxType.Blob, true, true, true, false)]
    [TestCase(TxType.SetCode, true, true, false, true)]
    [TestCase(TxType.DepositTx, false, false, false, false)]
    public void TxTypes_supported_functionality(TxType txType, bool supportAccessList, bool supportEip1559, bool supportBlob, bool supportSetCode)
    {
        Assert.That(txType.SupportsAccessList(), Is.EqualTo(supportAccessList));
        Assert.That(txType.Supports1559(), Is.EqualTo(supportEip1559));
        Assert.That(txType.SupportsBlobs(), Is.EqualTo(supportBlob));
        Assert.That(txType.SupportsAuthorizationList(), Is.EqualTo(supportSetCode));
    }
}
