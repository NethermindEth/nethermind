// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.Helpers;

public class QuorumCertificateBuilder : BuilderBase<QuorumCertificate>
{
    public QuorumCertificateBuilder()
    {
        TestObjectInternal = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[65]), new Signature(new byte[65])], 1);
    }

    public QuorumCertificateBuilder WithBlockInfo(BlockRoundInfo blockInfo)
    {
        TestObjectInternal.ProposedBlockInfo = blockInfo;
        return this;
    }

    public QuorumCertificateBuilder WithSignatures(params Signature[] signatures)
    {
        TestObjectInternal.Signatures = signatures;
        return this;
    }

    public QuorumCertificateBuilder WithGapNumber(ulong gapNumber)
    {
        TestObjectInternal.GapNumber = gapNumber;
        return this;
    }
}
