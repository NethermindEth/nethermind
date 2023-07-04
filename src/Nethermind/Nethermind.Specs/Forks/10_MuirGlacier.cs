// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class MuirGlacier : Istanbul
    {
        private static IReleaseSpec _instance;

        protected MuirGlacier()
        {
            Name = "Muir Glacier";
            DifficultyBombDelay = 9000000L;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new MuirGlacier());
    }
}
