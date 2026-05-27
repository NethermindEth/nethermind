// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class SyncInfo(QuorumCertificate highestQuorumCert, TimeoutCertificate highestTimeoutCert) : IEquatable<SyncInfo>
{
    public QuorumCertificate HighestQuorumCert { get; set; } = highestQuorumCert;
    public TimeoutCertificate HighestTimeoutCert { get; set; } = highestTimeoutCert;

    public bool Equals(SyncInfo? other) =>
        other is not null &&
        EqualityComparer<QuorumCertificate>.Default.Equals(HighestQuorumCert, other.HighestQuorumCert) &&
        EqualityComparer<TimeoutCertificate>.Default.Equals(HighestTimeoutCert, other.HighestTimeoutCert);

    public override bool Equals(object? obj) => Equals(obj as SyncInfo);

    public override int GetHashCode() => HashCode.Combine(HighestQuorumCert, HighestTimeoutCert);
}
