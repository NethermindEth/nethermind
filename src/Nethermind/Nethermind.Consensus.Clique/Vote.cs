// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Clique
{
    public class Vote(Address signer, long block, Address address, bool authorize)
    {
        public Address Signer { get; } = signer;
        public long Block { get; } = block;
        public Address Address { get; } = address;
        public bool Authorize { get; } = authorize;
    }
}
