// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    public class MevBundleTests
    {
        public static IEnumerable BundleTests
        {
            get
            {
                BundleTransaction BuildTransaction(PrivateKey sender, bool canRevert = false)
                {
                    BundleTransaction tx = Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(sender)
                        .TestObject;
                    tx.CanRevert = canRevert;
                    return tx;
                }

                BundleTransaction tx1 = BuildTransaction(TestItem.PrivateKeyA);
                BundleTransaction tx2 = BuildTransaction(TestItem.PrivateKeyB);
                MevBundle bundle = new(1, new[] { tx1, tx2 }, UInt256.One, UInt256.One);

                yield return new TestCaseData(bundle, new MevBundle(1, new[] { tx1, tx2 }))
                {
                    ExpectedResult = true,
                    TestName = "timestamps don't matter"
                };
                yield return new TestCaseData(bundle,
                    new MevBundle(1, new[] { BuildTransaction(TestItem.PrivateKeyA, true), tx2 }))
                {
                    ExpectedResult = true,
                    TestName = "reverting hashes don't matter"
                };
                yield return new TestCaseData(bundle, new MevBundle(1, new[] { tx2, tx1 }))
                {
                    ExpectedResult = false,
                    TestName = "transaction order matters"
                };
                yield return new TestCaseData(bundle, new MevBundle(2, new[] { tx1, tx2 }))
                {
                    ExpectedResult = false,
                    TestName = "block number matters"
                };

                BundleTransaction tx3 = BuildTransaction(TestItem.PrivateKeyC);
                BundleTransaction tx4 = BuildTransaction(TestItem.PrivateKeyD);
                yield return new TestCaseData(bundle, new MevBundle(2, new[] { tx3, tx4 }))
                {
                    ExpectedResult = false,
                    TestName = "transactions matter"
                };
            }
        }

        [TestCaseSource(nameof(BundleTests))]
        public bool bundles_are_identified_by_block_number_and_transactions(MevBundle bundle1, MevBundle bundle2) =>
            bundle1.Equals(bundle2);

        [Test]
        public void bundles_are_sequenced()
        {
            MevBundle bundle1 = new(1, new List<BundleTransaction>());
            MevBundle bundle2 = new(1, new List<BundleTransaction>());

            bundle2.SequenceNumber.Should().Be(bundle1.SequenceNumber + 1);
        }
    }
}
