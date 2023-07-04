// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.TxPool.Collections;

namespace Nethermind.Mev.Source
{
    public class BundleSortedPool : DistinctValueSortedPool<MevBundle, MevBundle, long>
    {
        public BundleSortedPool(int capacity, IComparer<MevBundle> comparer, ILogManager logManager)
            : base(capacity, comparer, EqualityComparer<MevBundle>.Default, logManager)
        {

        }

        protected override IComparer<MevBundle> GetUniqueComparer(IComparer<MevBundle> comparer) //compares all the bundles to evict the worst one
            => comparer.ThenBy(CompareMevBundleByHash.Default);

        protected override IComparer<MevBundle> GetGroupComparer(IComparer<MevBundle> comparer) //compares two bundles with same block #
            => comparer.ThenBy(CompareMevBundleByHash.Default);

        protected override long MapToGroup(MevBundle mevBundle) => mevBundle.BlockNumber;
        protected override MevBundle GetKey(MevBundle value) => value;

        protected override IComparer<MevBundle> GetReplacementComparer(IComparer<MevBundle> comparer) =>
            CompareMevBundleBySequenceNumber.Default;

        protected override bool AllowSameKeyReplacement => true;
    }
}
