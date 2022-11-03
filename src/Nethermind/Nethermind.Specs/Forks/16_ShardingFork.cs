// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    /// <summary>
    /// ShardingFork is a temporary name for a fork that potentially will be merged into Shanghai
    /// </summary>
    public class ShardingFork : Shanghai
    {
        private static IReleaseSpec _instance;

        protected ShardingFork()
        {
            Name = "ShardingFork";
            IsEip4844Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new ShardingFork());
    }
}
