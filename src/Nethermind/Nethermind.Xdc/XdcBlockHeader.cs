// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class XdcBlockHeader : BlockHeader
{
    public XdcBlockHeader(
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData)
        : base(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
    {
    }

    public byte[]? Validators { get; set; }
    public byte[]? Validator { get; set; }
    public byte[]? Penalties { get; set; }

    internal Address[] GetMasterNodesFromEpochSwitchHeader()
    {
        if (Validators == null)
            throw new InvalidOperationException("Header has no validators.");
        Address[] masterNodes = new Address[Validators.Length / 20];
        for (int i = 0; i < masterNodes.Length; i++)
        {
            masterNodes[i] = new Address(Validators.AsSpan(i * 20, 20));
        }
        return masterNodes;
    }
}
