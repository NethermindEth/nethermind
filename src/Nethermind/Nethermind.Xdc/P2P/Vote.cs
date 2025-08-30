// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class Vote
{
    public Address Signer { get; set; }
    public BlockInfo ProposedBlockInfo { get; set; }
    public Signature Signature { get; set; }
    public ulong gapNumber { get; set; }
}
