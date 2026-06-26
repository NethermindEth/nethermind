// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Xdc;

public interface IQuorumCertificateManager
{
    QuorumCertificate HighestKnownCertificate { get; }
    QuorumCertificate? LockCertificate { get; }

    void CommitCertificate(QuorumCertificate qc);
    bool VerifyCertificate(QuorumCertificate qc, XdcBlockHeader certificateTarget, [NotNullWhen(false)] out string? error);
    bool VerifyCertificate(QuorumCertificate qc, [NotNullWhen(false)] out string? error);
    void Initialize(XdcBlockHeader current);
}
