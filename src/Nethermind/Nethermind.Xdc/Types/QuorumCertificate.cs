// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using System.Text;

namespace Nethermind.Xdc.Types;

public class QuorumCertificate(BlockRoundInfo proposedBlockInfo, Signature[]? signatures, ulong gapNumber) : RlpHashEqualityBase
{
    private static readonly QuorumCertificateDecoder _decoder = new();
    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public Signature[] Signatures { get; set; } = signatures;
    public ulong GapNumber { get; set; } = gapNumber;

    protected override void Encode(KeccakRlpStream stream) => _decoder.Encode(stream, this, RlpBehaviors.None);
}
