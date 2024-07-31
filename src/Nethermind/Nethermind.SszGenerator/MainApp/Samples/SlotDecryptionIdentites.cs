// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MainApp.Samples;

// https://github.com/NethermindEth/nethermind/pull/6466/files#diff-e79e1fe983e5962d17e319c799805006cea067a3db0af81a4e6c55a5869424c1R11
public struct SlotDecryptionIdentites
{
    public ulong InstanceID;
    public ulong Eon;
    public ulong Slot;
    public ulong TxPointer;
    public List<byte[]> IdentityPreimages;
}
