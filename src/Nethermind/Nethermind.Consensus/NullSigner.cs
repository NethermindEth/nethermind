// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public class NullSigner : ISigner, ISignerStore
    {
        public static NullSigner Instance { get; } = new();

        public Address Address { get; } = Address.Zero; // TODO: why zero address

        public ValueTask Sign(Transaction tx) => default;

        public Signature Sign(Hash256 message) { return new(new byte[65]); }

        public bool CanSign { get; } = true; // TODO: why true?

        public PrivateKey? Key { get; } = null;

        public bool CanSignHeader => false;

        public void SetSigner(PrivateKey key) { }

        public void SetSigner(ProtectedPrivateKey key) { }

        public Signature Sign(BlockHeader header) { return new(new byte[65]); }
    }
}
