// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Constantinople : Byzantium
    {
        private static IReleaseSpec _instance;

        protected Constantinople()
        {
            Name = "Constantinople";
            BlockReward = UInt256.Parse("2000000000000000000");
            DifficultyBombDelay = 5000000L;
            IsEip145Enabled = true;
            IsEip1014Enabled = true;
            IsEip1052Enabled = true;
            IsEip1283Enabled = true;
            IsEip1234Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Constantinople());
    }
}
