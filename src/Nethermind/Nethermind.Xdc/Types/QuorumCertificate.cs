// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class QuorumCertificate(BlockRoundInfo proposedBlockInfo, Signature[]? signatures, ulong gapNumber) : RlpHashEqualityBase
{
    private static readonly QuorumCertificateDecoder _decoder = new();
    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public Signature[] Signatures { get; set; } = signatures;
    public ulong GapNumber { get; set; } = gapNumber;

    protected override void Encode(ref KeccakRlpWriter writer) =>
        _decoder.Encode(ref writer, this, RlpBehaviors.None);
}
