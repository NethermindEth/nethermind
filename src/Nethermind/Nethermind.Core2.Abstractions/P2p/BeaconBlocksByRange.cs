// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.P2p
{
    public class BeaconBlocksByRange
    {
        public BeaconBlocksByRange(Root headBlockRoot, Slot startSlot, ulong count, ulong step)
        {
            HeadBlockRoot = headBlockRoot;
            StartSlot = startSlot;
            Count = count;
            Step = step;
        }

        public ulong Count { get; }
        public Root HeadBlockRoot { get; }
        public Slot StartSlot { get; }
        public ulong Step { get; }

        public override string ToString()
        {
            return $"hr={HeadBlockRoot.ToString().Substring(0, 10)}_ss={StartSlot}";
        }
    }
}
