// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Clique
{
    public class Vote(Address signer, ulong block, Address address, bool authorize)
    {
        public Address Signer { get; } = signer;
        public ulong Block { get; } = block;
        public Address Address { get; } = address;
        public bool Authorize { get; } = authorize;
    }
}
