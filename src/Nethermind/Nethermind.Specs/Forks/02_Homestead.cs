// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Homestead : Frontier
    {
        private static IReleaseSpec _instance;

        protected Homestead()
        {
            Name = "Homestead";
            IsEip2Enabled = true;
            IsEip7Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Homestead());
    }
}
