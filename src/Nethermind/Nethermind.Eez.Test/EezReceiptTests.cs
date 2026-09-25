// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezReceiptTests
{
    [Test]
    public void Encode_SystemTransactionReceipt_IsTypedWithTheSystemTransactionType()
    {
        ReceiptMessageDecoder decoder = new();
        TxReceipt legacy = Build.A.Receipt.WithTxType(TxType.Legacy).WithStatusCode(1).WithGasUsedTotal(42_000).WithLogs(Build.A.LogEntry.TestObject).TestObject;
        TxReceipt system = Build.A.Receipt.WithTxType(EezConstants.SystemTxType).WithStatusCode(1).WithGasUsedTotal(42_000).WithLogs(Build.A.LogEntry.TestObject).TestObject;

        byte[] legacyEncoding = decoder.EncodeNew(legacy, RlpBehaviors.SkipTypedWrapping);
        byte[] systemEncoding = decoder.EncodeNew(system, RlpBehaviors.SkipTypedWrapping);

        Assert.That(systemEncoding, Is.EqualTo(Bytes.Concat((byte)EezConstants.SystemTxType, legacyEncoding)),
            "the receipts root commits to 0x76 || rlp([status, cumulativeGas, bloom, logs])");
    }
}
