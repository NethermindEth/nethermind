// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Clique
{
    public class Tally(bool authorize)
    {
        public bool Authorize { get; } = authorize;
        public int Votes { get; set; }
    }
}
