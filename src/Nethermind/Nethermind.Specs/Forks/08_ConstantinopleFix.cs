// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class ConstantinopleFix : Constantinople
    {
        private static IReleaseSpec _instance;

        protected ConstantinopleFix()
        {
            Name = "Constantinople Fix";
            IsEip1283Enabled = false;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new ConstantinopleFix());
    }
}
