// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{
    public class ReceiptsRootTests
    {
        public static IEnumerable ReceiptsRootTestCases
        {
            get
            {
                Keccak skipHash = new("0x8f4aebb6fea8f70b5fb5fcc578d8ad7584caed6f662b475702ef964e95f8a885");
                Keccak properHash = new("0xe51a2d9f986d68628990c9d65e45c36128ec7bb697bd426b0bb4d18a3f3321be");
                yield return new TestCaseData(true, skipHash).Returns(properHash);
                yield return new TestCaseData(false, skipHash).Returns(skipHash);
                yield return new TestCaseData(false, Keccak.Zero).Returns(properHash);
            }
        }

        [TestCaseSource(nameof(ReceiptsRootTestCases))]
        public Keccak Should_Calculate_ReceiptsRoot(bool validateReceipts, Keccak suggestedRoot)
        {

            TxReceipt[] receipts = { Build.A.Receipt.WithAllFieldsFilled.TestObject };
            return receipts.GetReceiptsRoot(new ReleaseSpec() { ValidateReceipts = validateReceipts }, suggestedRoot);
        }
    }
}
