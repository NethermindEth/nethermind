// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs
    {
        public PathWithStorageSlot[][] PathsAndSlots { get; set; }
        public byte[][] Proofs { get; set; }
    }
}
