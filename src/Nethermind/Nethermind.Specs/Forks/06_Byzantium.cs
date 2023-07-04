// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Byzantium : SpuriousDragon
    {
        private static IReleaseSpec _instance;

        protected Byzantium()
        {
            Name = "Byzantium";
            BlockReward = UInt256.Parse("3000000000000000000");
            DifficultyBombDelay = 3000000L;
            IsEip100Enabled = true;
            IsEip140Enabled = true;
            IsEip196Enabled = true;
            IsEip197Enabled = true;
            IsEip198Enabled = true;
            IsEip211Enabled = true;
            IsEip214Enabled = true;
            IsEip649Enabled = true;
            IsEip658Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Byzantium());
    }
}
