// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessage : SnapMessageBase
    {
        public ByteCodesMessage(IDisposableReadOnlyList<byte[]>? data)
        {
            Codes = data ?? ArrayPoolList<byte[]>.Empty();
        }

        public override int PacketType => SnapMessageCode.ByteCodes;

        public IDisposableReadOnlyList<byte[]> Codes { get; }
    }
}
