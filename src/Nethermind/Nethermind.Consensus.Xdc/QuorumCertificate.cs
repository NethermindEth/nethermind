// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Xdc;
internal class QuorumCertificate
{
    public QuorumCertificate()
    {
    }

    public BlockInfo ProposedBlockInfo { get; set; }
    public Signature[] Signatures { get; set; }
    public ulong GapNumber { get; set; }
}
