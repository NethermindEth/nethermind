// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Errors;
internal class QuorumCertificateException : BlockchainException
{
    public QuorumCertificateException(QuorumCertificate certificate, string message) : base(message)
    {
        Certificate = certificate;
    }

    public QuorumCertificate Certificate { get; }
}
