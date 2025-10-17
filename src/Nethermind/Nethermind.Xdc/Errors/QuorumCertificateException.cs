// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.Errors;
internal class QuorumCertificateException(QuorumCertificate certificate, string message) : BlockchainException(message)
{
    public QuorumCertificate Certificate { get; } = certificate;
}
