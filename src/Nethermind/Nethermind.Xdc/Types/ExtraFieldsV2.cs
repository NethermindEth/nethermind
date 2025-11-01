// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class ExtraFieldsV2(ulong round, QuorumCertificate quorumCert)
{
    public ulong BlockRound { get; } = round;
    public QuorumCertificate QuorumCert { get; } = quorumCert;
}
