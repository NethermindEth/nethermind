// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Api
{
    public class SyncingStatus
    {
        public static SyncingStatus Zero = new SyncingStatus(Slot.Zero, Slot.Zero, Slot.Zero);

        public SyncingStatus(Slot startingSlot, Slot currentSlot, Slot highestSlot)
        {
            StartingSlot = startingSlot;
            CurrentSlot = currentSlot;
            HighestSlot = highestSlot;
        }

        public Slot CurrentSlot { get; }
        public Slot HighestSlot { get; }
        public Slot StartingSlot { get; }
    }
}
