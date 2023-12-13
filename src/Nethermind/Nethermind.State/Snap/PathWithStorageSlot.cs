// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithStorageSlot
    {
        public PathWithStorageSlot(ValueHash256 keyHash, byte[] slotRlpValue)
        {
            Path = keyHash;
            SlotRlpValue = slotRlpValue;
        }

        public ValueHash256 Path { get; set; }
        public byte[] SlotRlpValue { get; set; }
    }
}
