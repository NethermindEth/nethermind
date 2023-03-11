// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Mev.Test
{
    public class TestMevSigner : ISigner
    {
        public TestMevSigner(Address blockAuthorAddress)
        {
            Address = blockAuthorAddress;
        }

        public ValueTask Sign(Transaction tx) => default;

        public PrivateKey Key => null!;

        public Address Address { get; }

        public Signature Sign(Keccak message) => null!;

        public bool CanSign => true;
    }
}
