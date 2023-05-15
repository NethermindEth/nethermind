// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetByteCodesMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.GetByteCodes;

        /// <summary>
        /// Code hashes to retrieve the code for
        /// </summary>
        public IReadOnlyList<ValueKeccak> Hashes { get; set; }

        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long Bytes { get; set; }
    }
}
