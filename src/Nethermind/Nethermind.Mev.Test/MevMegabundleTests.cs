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
    public class MevMegabundleTests
    {
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
                MevMegabundle megabundle = new(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, null, UInt256.One,
                    UInt256.One);
                Signature relaySignature = ecdsa.Sign(TestItem.PrivateKeyA, megabundle.Hash);
                megabundle.RelaySignature = relaySignature;
                
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {BuildTransaction(TestItem.PrivateKeyA), tx}, new[] {revertingTx.Hash!},
                        relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = true, TestName = "reverting tx don't matter"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, relaySignature: relaySignature,
                        minTimestamp: UInt256.One, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false, TestName = "reverting tx hashes matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, relaySignature, UInt256.One))
                {
                    ExpectedResult = false, TestName = "max timestamp matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, relaySignature, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false, TestName = "min timestamp matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, minTimestamp: UInt256.One, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false, TestName = "relay signature matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] {tx, revertingTx}, new[] {revertingTx.Hash!}, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false, TestName = "transaction order matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(2, new[] {revertingTx, tx}, new[] {revertingTx.Hash!}, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false, TestName = "block number matters"
                };
                
                BundleTransaction tx3 = BuildTransaction(TestItem.PrivateKeyC);
                BundleTransaction tx4 = BuildTransaction(TestItem.PrivateKeyD);
                yield return new TestCaseData(megabundle, new MevMegabundle(2, new[] {tx3, tx4}, new[] {revertingTx.Hash!}, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false, TestName = "transactions matter"
                };
            }
        }

        [TestCaseSource(nameof(MegabundleTests))]
        public bool megabundles_are_identified_by_block_number_and_transactions(MevMegabundle bundle1,
            MevMegabundle bundle2) => bundle1.Equals(bundle2);
    }
}
