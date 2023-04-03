// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Clique
{
    public class Vote
    {
        public Address Signer { get; }
        public long Block { get; }
        public Address Address { get; }
        public bool Authorize { get; }

        public Vote(Address signer, long block, Address address, bool authorize)
        {
            Signer = signer;
            Block = block;
            Address = address;
            Authorize = authorize;
        }
    }
}
