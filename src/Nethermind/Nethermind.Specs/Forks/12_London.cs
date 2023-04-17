// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class London : Berlin
    {
        private static IReleaseSpec _instance;

        protected London()
        {
            Name = "London";
            DifficultyBombDelay = 9700000L;
            IsEip1559Enabled = true;
            IsEip3198Enabled = true;
            IsEip3529Enabled = true;
            IsEip3541Enabled = true;
            Eip1559TransitionBlock = 12965000;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new London());
    }
}
