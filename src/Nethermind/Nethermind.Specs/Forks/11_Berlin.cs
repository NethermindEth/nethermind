// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class Berlin : MuirGlacier
    {
        private static IReleaseSpec _instance;

        protected Berlin()
        {
            Name = "Berlin";
            IsEip2565Enabled = true;
            IsEip2929Enabled = true;
            IsEip2930Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Berlin());
    }
}
