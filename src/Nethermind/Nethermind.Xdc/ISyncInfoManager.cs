// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public interface ISyncInfoManager
{
    /// <summary>Verifies the quorum certificate of a received <see cref="SyncInfo"/> and commits it if it passes.</summary>
    /// <returns>Why the certificate was skipped, or <c>null</c> when it was committed.</returns>
    string? ProcessQuorumCertificate(QuorumCertificate quorumCert);

    /// <summary>Verifies the timeout certificate of a received <see cref="SyncInfo"/> and applies it if it passes.</summary>
    /// <returns>Why the certificate was skipped, or <c>null</c> when it was applied.</returns>
    string? ProcessTimeoutCertificate(TimeoutCertificate timeoutCert);

    SyncInfo GetSyncInfo();
}
