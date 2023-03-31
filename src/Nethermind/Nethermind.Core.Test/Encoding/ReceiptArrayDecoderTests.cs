// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
#pragma warning disable 618

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ReceiptArrayDecoderTests
    {
        [Test]
        public void Can_do_roundtrip_storage(
            [Values(RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts, RlpBehaviors.Storage)] RlpBehaviors encodeBehaviors,
            [Values(true, false)] bool compactEncoding,
            [Values(true, false)] bool withError
        )
        {
            TxReceipt GetExpected()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;

                if ((encodeBehaviors & RlpBehaviors.Eip658Receipts) != 0)
                {
                    receiptBuilder.WithState(null!);
                }
                else
                {
                    receiptBuilder.WithStatusCode(0);
                }

                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                if (compactEncoding)
                {
                    receiptBuilder.WithBlockHash(null);
                    receiptBuilder.WithBlockNumber(0);
                    receiptBuilder.WithTxType(0);
                    receiptBuilder.WithTransactionHash(null);
                    receiptBuilder.WithIndex(0);
                    receiptBuilder.WithGasUsed(0);
                    receiptBuilder.WithContractAddress(null);
                    receiptBuilder.WithRecipient(null);
                    receiptBuilder.WithError(null);
                }

                receiptBuilder.WithCalculatedBloom();
                return receiptBuilder.TestObject;
            }

            TxReceipt[] GetExpectedArray()
            {
                return new[] { GetExpected(), GetExpected() };
            }

            TxReceipt BuildReceipt()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                return receiptBuilder.TestObject;
            }

            TxReceipt[] txReceipts = { BuildReceipt(), BuildReceipt() };

            ReceiptArrayStorageDecoder encoder = new(compactEncoding);
            using NettyRlpStream rlp = encoder.EncodeToNewNettyStream(txReceipts, encodeBehaviors);

            ReceiptArrayStorageDecoder decoder = new();
            TxReceipt[] deserialized = decoder.Decode(rlp, RlpBehaviors.Storage);

            deserialized.Should().BeEquivalentTo(GetExpectedArray());
        }
    }
}
