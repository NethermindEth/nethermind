// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Bundler;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class MevBundlerTests
    {
        private ITxSource GetTxSource(params Transaction[] transactions)
        {
            var txSource = Substitute.For<ITxSource>();
            txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>()).Returns(transactions);
            return txSource;
        }

        private IBundlePool GetBundlePool(List<MevBundle> bundles)
        {
            var bundlePool = Substitute.For<IBundlePool>();
            bundlePool.AddBundle(Arg.Any<MevBundle>()).Returns(true).AndDoes(info => bundles.Add(info.Arg<MevBundle>()));
            bundlePool.GetBundles(Arg.Any<long>(), Arg.Any<UInt256>()).Returns(bundles);
            return bundlePool;
        }

        private IBundleTrigger GetBundleTrigger()
        {
            return Substitute.For<IBundleTrigger>();
        }

        [Test]
        public void adds_bundles_to_mev_pool_when_mev_plugin_is_enabled()
        {
            var transactions = new Transaction[] { };

            var bundleTrigger = GetBundleTrigger();
            var txSource = GetTxSource(transactions);

            var bundles = new List<MevBundle>();
            var bundlePool = GetBundlePool(bundles);

            var bundler = new MevBundler(bundleTrigger, txSource, bundlePool, NullLogger.Instance);
            var bundleEventArgs = new BundleUserOpsEventArgs(Core.Test.Builders.Build.A.Block.TestObject);

            bundleTrigger.TriggerBundle += Raise.EventWith(this, bundleEventArgs);

            var bundledTxs = bundles.SelectMany(bundle => bundle.Transactions);
            Assert.That(bundledTxs.SequenceEqual(transactions));
        }
    }
}
