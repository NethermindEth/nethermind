// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithStorageSlot
    {
        public PathWithStorageSlot(ValueKeccak keyHash, byte[] slotRlpValue)
        {
            Path = keyHash;
            SlotRlpValue = slotRlpValue;
        }

        public ValueKeccak Path { get; set; }
        public byte[] SlotRlpValue { get; set; }
    }
}
