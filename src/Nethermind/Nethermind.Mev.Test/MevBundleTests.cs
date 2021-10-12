//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
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
                MevBundle bundle = new(1, new[] {tx1, tx2}, UInt256.One, UInt256.One);

                yield return new TestCaseData(bundle, new MevBundle(1, new[] {tx1, tx2}))
                {
                    ExpectedResult = true, TestName = "timestamps don't matter"
                };
                yield return new TestCaseData(bundle,
                    new MevBundle(1, new[] {BuildTransaction(TestItem.PrivateKeyA, true), tx2}))
                {
                    ExpectedResult = true, TestName = "reverting hashes don't matter"
                };
                yield return new TestCaseData(bundle, new MevBundle(1, new[] {tx2, tx1}))
                {
                    ExpectedResult = false, TestName = "transaction order matters"
                };
                yield return new TestCaseData(bundle, new MevBundle(2, new[] {tx1, tx2}))
                {
                    ExpectedResult = false, TestName = "block number matters"
                };

                BundleTransaction tx3 = BuildTransaction(TestItem.PrivateKeyC);
                BundleTransaction tx4 = BuildTransaction(TestItem.PrivateKeyD);
                yield return new TestCaseData(bundle, new MevBundle(2, new[] {tx3, tx4}))
                {
                    ExpectedResult = false, TestName = "transactions matter"
                };
            }
        }

        [TestCaseSource(nameof(BundleTests))]
        public bool bundles_are_identified_by_block_number_and_transactions(MevBundle bundle1, MevBundle bundle2) =>
            bundle1.Equals(bundle2);

        public static IEnumerable MegabundleTests
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

                EthereumEcdsa ecdsa = new(ChainId.Mainnet, LimboLogs.Instance);

                BundleTransaction tx = BuildTransaction(TestItem.PrivateKeyB);
                BundleTransaction revertingTx = BuildTransaction(TestItem.PrivateKeyA, true);
                Keccak[] revertingTxHashes = {revertingTx.Hash!};
                MevMegabundle megabundle = new(1, new[] {revertingTx, tx}, revertingTxHashes, null, UInt256.One,
                    UInt256.One);
                Signature relaySignature = ecdsa.Sign(TestItem.PrivateKeyA, megabundle.Hash);
                megabundle.RelaySignature = relaySignature;

                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, relaySignature))
                {
                    ExpectedResult = true, TestName = "timestamps don't matter"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {BuildTransaction(TestItem.PrivateKeyA), tx}, new[] {revertingTx.Hash!},
                        relaySignature)) {ExpectedResult = true, TestName = "reverting tx don't matter"};
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, relaySignature: relaySignature))
                {
                    ExpectedResult = false, TestName = "reverting tx hashes matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}))
                {
                    ExpectedResult = false, TestName = "relay signature matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {tx, revertingTx}, new[] {revertingTx.Hash!}, relaySignature))
                {
                    ExpectedResult = false, TestName = "transaction order matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(2, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, relaySignature))
                {
                    ExpectedResult = false, TestName = "block number matters"
                };

                BundleTransaction tx3 = BuildTransaction(TestItem.PrivateKeyC);
                BundleTransaction tx4 = BuildTransaction(TestItem.PrivateKeyD);
                yield return new TestCaseData(megabundle, new MevBundle(2, new[] {tx3, tx4}))
                {
                    ExpectedResult = false, TestName = "transactions matter"
                };
            }
        }

        [TestCaseSource(nameof(MegabundleTests))]
        public bool megabundles_are_identified_by_block_number_and_transactions(MevMegabundle bundle1,
            MevMegabundle bundle2) => bundle1.Equals(bundle2);

        [Test]
        public void bundles_are_sequenced()
        {
            MevBundle bundle1 = new(1, new List<BundleTransaction>());
            MevBundle bundle2 = new(1, new List<BundleTransaction>());

            bundle2.SequenceNumber.Should().Be(bundle1.SequenceNumber + 1);
        }
    }
}
