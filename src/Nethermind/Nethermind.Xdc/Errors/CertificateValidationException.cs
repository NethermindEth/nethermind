// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Errors;

public enum CertificateType
{
    QuorumCertificate,
    TimeoutCertificate
}

public enum CertificateValidationFailure
{
    InvalidContent,
    InvalidSignatures
}

[Serializable]
public class CertificateValidationException : Exception
{
    public CertificateValidationException(CertificateType certificateType, CertificateValidationFailure failure, Exception? innerException = null)
        : base($"Invalid {certificateType}: {failure}", innerException)
    {
        CertificateType = certificateType;
        Failure = failure;
    }

    public CertificateType CertificateType { get; }
    public CertificateValidationFailure Failure { get; }
}
