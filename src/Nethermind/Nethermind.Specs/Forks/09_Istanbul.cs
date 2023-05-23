// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Istanbul : ConstantinopleFix
    {
        private static IReleaseSpec _instance;

        protected Istanbul()
        {
            Name = "Istanbul";
            IsEip1344Enabled = true;
            IsEip2028Enabled = true;
            IsEip152Enabled = true;
            IsEip1108Enabled = true;
            IsEip1884Enabled = true;
            IsEip2200Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Istanbul());
    }
}
