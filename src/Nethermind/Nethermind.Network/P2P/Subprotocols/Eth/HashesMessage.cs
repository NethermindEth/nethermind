// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessage : P2PMessage
    {
        protected HashesMessage(IOwnedReadOnlyList<Hash256> hashes)
        {
            Hashes = hashes ?? throw new ArgumentNullException(nameof(hashes));
        }

        public IOwnedReadOnlyList<Hash256> Hashes { get; }

        public override string ToString()
        {
            return $"{GetType().Name}({Hashes.Count})";
        }

        public override void Dispose()
        {
            base.Dispose();
            Hashes.Dispose();
        }
    }
}
