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
                foreach (Transaction transaction in bundle.Transactions)
                {
                    yield return transaction;
                }
            }

        }
    }
}
