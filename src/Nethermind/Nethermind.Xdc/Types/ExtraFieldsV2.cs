// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class ExtraFieldsV2(ulong round, QuorumCertificate quorumCert) : IEquatable<ExtraFieldsV2>
{
    public ulong BlockRound { get; } = round;
    public QuorumCertificate QuorumCert { get; } = quorumCert;

    public bool Equals(ExtraFieldsV2? other) =>
        other is not null &&
        BlockRound == other.BlockRound &&
        EqualityComparer<QuorumCertificate>.Default.Equals(QuorumCert, other.QuorumCert);

    public override bool Equals(object? obj) => Equals(obj as ExtraFieldsV2);

    public override int GetHashCode() => HashCode.Combine(BlockRound, QuorumCert);
}
