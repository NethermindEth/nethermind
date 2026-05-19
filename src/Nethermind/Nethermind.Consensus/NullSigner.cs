// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public class NullSigner : IHeaderSigner, ISignerStore
    {
        public static NullSigner Instance { get; } = new();

        public Address Address { get; } = Address.Zero;

        public bool TrySign(Transaction tx) => false;

        public bool TrySign(in ValueHash256 message, [NotNullWhen(true)] out Signature signature)
        {
            signature = null!;
            return false;
        }

        public bool TrySign(BlockHeader header, [NotNullWhen(true)] out Signature signature)
        {
            signature = null!;
            return false;
        }

        public bool CanSign { get; } = false;

        public PrivateKey? Key { get; } = null;

        public bool CanSignHeader => false;

        public void SetSigner(PrivateKey key) { }

        public void SetSigner(IProtectedPrivateKey key) { }
    }
}
