// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

public class QuorumCert(BlockInfo proposedBlockInfo, Signature[] signatures, ulong gapNumber)
{
    public BlockInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public Signature[] Signatures { get; set; } = signatures;
    public ulong GapNumber { get; set; } = gapNumber;
}
