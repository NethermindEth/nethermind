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

    public enum TxFeatureSupport
    {
        None = 0,
        AccessList = 1,
        EIP1559 = 2,
        Blob = 4,
        SetCode = 8
    }

    [TestCase(TxType.Legacy, TxFeatureSupport.None)]
    [TestCase(TxType.AccessList, TxFeatureSupport.AccessList)]
    [TestCase(TxType.EIP1559, TxFeatureSupport.AccessList | TxFeatureSupport.EIP1559)]
    [TestCase(TxType.Blob, TxFeatureSupport.AccessList | TxFeatureSupport.EIP1559 | TxFeatureSupport.Blob)]
    [TestCase(TxType.SetCode, TxFeatureSupport.AccessList | TxFeatureSupport.EIP1559 | TxFeatureSupport.SetCode)]
    [TestCase(TxType.DepositTx, TxFeatureSupport.None)]
    public void TxTypes_supported_functionality(TxType txType, TxFeatureSupport expectedFeaturesSupport)
    {
        Assert.That(txType.SupportsAccessList(), Is.EqualTo(expectedFeaturesSupport.HasFlag(TxFeatureSupport.AccessList)));
        Assert.That(txType.Supports1559(), Is.EqualTo(expectedFeaturesSupport.HasFlag(TxFeatureSupport.EIP1559)));
        Assert.That(txType.SupportsBlobs(), Is.EqualTo(expectedFeaturesSupport.HasFlag(TxFeatureSupport.Blob)));
        Assert.That(txType.SupportsAuthorizationList(), Is.EqualTo(expectedFeaturesSupport.HasFlag(TxFeatureSupport.SetCode)));
    }
}
