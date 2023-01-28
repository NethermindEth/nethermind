// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class Shanghai : GrayGlacier
    {
        private static IReleaseSpec _instance;

        protected Shanghai()
        {
            Name = "Shanghai";
            IsEip3651Enabled = true;
            IsEip3855Enabled = true;
            IsEip3860Enabled = true;
            IsEip4895Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Shanghai());
    }
}
