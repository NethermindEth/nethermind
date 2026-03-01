// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ReceiptMessageDecoderTests
{
    [Test]
    public void TestGlobalReceiptEncoderMustBeReceiptMessageDecoder()
    {
        Assert.That(Rlp.GetStreamEncoder<TxReceipt>(), Is.InstanceOf<ReceiptMessageDecoder>());
        Assert.That(Rlp.GetStreamEncoder<LogEntry>(), Is.InstanceOf<LogEntryDecoder>());
    }
}
