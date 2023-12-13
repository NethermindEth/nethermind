// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class ArrowGlacier : London
    {
        private static IReleaseSpec _instance;

        protected ArrowGlacier()
        {
            Name = "ArrowGlacier";
            DifficultyBombDelay = 10700000L;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new ArrowGlacier());
    }
}
