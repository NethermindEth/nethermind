// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessage68 : P2PMessage
    {
        // we are able to safely send message with up to 2925 hashes+types+lengths to not exceed message size
        // of 102400 bytes which is used by Geth and us as max message size. (2925 items message has 102385 bytes)
        public const int MaxCount = 2048;

        public override int PacketType { get; } = Eth68MessageCode.NewPooledTransactionHashes;
        public override string Protocol { get; } = "eth";

        public readonly IDisposableReadOnlyList<byte> Types;
        public readonly IDisposableReadOnlyList<int> Sizes;
        public readonly IDisposableReadOnlyList<Hash256> Hashes;

        public NewPooledTransactionHashesMessage68(
            IDisposableReadOnlyList<byte> types,
            IDisposableReadOnlyList<int> sizes,
            IDisposableReadOnlyList<Hash256> hashes
        )
        {
            Types = types;
            Sizes = sizes;
            Hashes = hashes;
        }

        public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage68)}({Hashes.Count})";

        public override void Dispose()
        {
            base.Dispose();
            Types.Dispose();
            Sizes.Dispose();
            Hashes.Dispose();
        }
    }
}
