// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithStorageSlot
    {
        public PathWithStorageSlot(ValueCommitment keyHash, byte[] slotRlpValue)
        {
            Path = keyHash;
            SlotRlpValue = slotRlpValue;
        }

        public ValueCommitment Path { get; set; }
        public byte[] SlotRlpValue { get; set; }
    }
}
