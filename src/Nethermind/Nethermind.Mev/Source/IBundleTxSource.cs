// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class BundleTxSource : ITxSource
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        private readonly IBundleSource _bundleSource;
        private readonly ITimestamper _timestamper;
        private readonly TimeSpan _timeout;

        public BundleTxSource(IBundleSource bundleSource, ITimestamper timestamper, TimeSpan? timeout = null)
        {
            _bundleSource = bundleSource;
            _timestamper = timestamper;
            _timeout = timeout ?? DefaultTimeout;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            using CancellationTokenSource cancellationTokenSource = new(_timeout);
            Task<IEnumerable<MevBundle>> bundlesTasks = _bundleSource.GetBundles(parent, _timestamper.UnixTime.Seconds, gasLimit, cancellationTokenSource.Token);
            IEnumerable<MevBundle> bundles = bundlesTasks.Result; // Is it ok as it will timeout on cancellation token and not create a deadlock?
            foreach (MevBundle bundle in bundles)
            {
                foreach (BundleTransaction transaction in bundle.Transactions)
                {
                    yield return transaction;
                }
            }

        }
    }
}
