// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs : IDisposable
    {
        public IOwnedReadOnlyList<PathWithStorageSlot[]> PathsAndSlots { get; set; }
        public IOwnedReadOnlyList<byte[]> Proofs { get; set; }

        public void Dispose()
        {
            PathsAndSlots?.Dispose();
            Proofs?.Dispose();
        }
    }
}
