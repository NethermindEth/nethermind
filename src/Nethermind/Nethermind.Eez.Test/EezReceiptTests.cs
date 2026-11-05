// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezReceiptTests
{
    [Test]
    public void Encode_SystemTransactionReceipt_IsTypedWithTheSystemTransactionType([Values((byte)EezConstants.SystemTxType, (byte)EezConstants.NativeSystemTxType)] byte type)
    {
        ReceiptMessageDecoder decoder = new();
        TxReceipt legacy = Build.A.Receipt.WithTxType(TxType.Legacy).WithStatusCode(1).WithGasUsedTotal(42_000).WithLogs(Build.A.LogEntry.TestObject).TestObject;
        TxReceipt system = Build.A.Receipt.WithTxType((TxType)type).WithStatusCode(1).WithGasUsedTotal(42_000).WithLogs(Build.A.LogEntry.TestObject).TestObject;

        byte[] legacyEncoding = decoder.EncodeNew(legacy, RlpBehaviors.SkipTypedWrapping);
        byte[] systemEncoding = decoder.EncodeNew(system, RlpBehaviors.SkipTypedWrapping);

        Assert.That(systemEncoding, Is.EqualTo(Bytes.Concat(type, legacyEncoding)),
            "the receipts root commits to the system type followed by the ordinary receipt payload");
    }

    [Test]
    public void Encode_NativeReceipt_IndependentVectorMatchesBytesAndHash()
    {
        JsonElement vector = StatelessFixtures.ReadJson("rollupx/native-profile-v1", "vectors.json").GetProperty("vectors")
            .EnumerateArray().Single(item => item.GetProperty("id").GetString() == "native-success-receipt");
        TxReceipt receipt = new() { TxType = EezConstants.NativeSystemTxType, StatusCode = 1, GasUsedTotal = 21_000, Bloom = Bloom.Empty, Logs = [] };
        byte[] encoded = new ReceiptMessageDecoder().EncodeNew(receipt, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.Eip658Receipts);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(Keccak.Compute(encoded), Is.EqualTo(new Hash256(vector.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

}
