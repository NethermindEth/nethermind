// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

                EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet, LimboLogs.Instance);

                BundleTransaction tx = BuildTransaction(TestItem.PrivateKeyB);
                BundleTransaction revertingTx = BuildTransaction(TestItem.PrivateKeyA, true);
                MevMegabundle megabundle = new(1, new[] { revertingTx, tx }, new[] { revertingTx.Hash! }, null, UInt256.One,
                    UInt256.One);
                Signature relaySignature = ecdsa.Sign(TestItem.PrivateKeyA, megabundle.Hash);
                megabundle.RelaySignature = relaySignature;

                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { BuildTransaction(TestItem.PrivateKeyA), tx }, new[] { revertingTx.Hash! },
                        relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = true,
                    TestName = "reverting tx don't matter"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { revertingTx, tx }, relaySignature: relaySignature,
                        minTimestamp: UInt256.One, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "reverting tx hashes matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { revertingTx, tx }, new[] { revertingTx.Hash! }, relaySignature, UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "max timestamp matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { revertingTx, tx }, new[] { revertingTx.Hash! }, relaySignature, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "min timestamp matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { revertingTx, tx }, new[] { revertingTx.Hash! }, minTimestamp: UInt256.One, maxTimestamp: UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "relay signature matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(1, new[] { tx, revertingTx }, new[] { revertingTx.Hash! }, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "transaction order matters"
                };
                yield return new TestCaseData(megabundle,
                    new MevMegabundle(2, new[] { revertingTx, tx }, new[] { revertingTx.Hash! }, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "block number matters"
                };

                BundleTransaction tx3 = BuildTransaction(TestItem.PrivateKeyC);
                BundleTransaction tx4 = BuildTransaction(TestItem.PrivateKeyD);
                yield return new TestCaseData(megabundle, new MevMegabundle(2, new[] { tx3, tx4 }, new[] { revertingTx.Hash! }, relaySignature, UInt256.One, UInt256.One))
                {
                    ExpectedResult = false,
                    TestName = "transactions matter"
                };
            }
        }

        [TestCaseSource(nameof(MegabundleTests))]
        public bool megabundles_are_identified_by_block_number_and_transactions(MevMegabundle bundle1,
            MevMegabundle bundle2) => bundle1.Equals(bundle2);
    }
}
