// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessage : P2PMessage
    {
        protected HashesMessage(IReadOnlyList<Keccak> hashes)
        {
            Hashes = hashes ?? throw new ArgumentNullException(nameof(hashes));
        }

        public IReadOnlyList<Keccak> Hashes { get; }

        public override string ToString()
        {
            return $"{GetType().Name}({Hashes.Count})";
        }
    }
}
