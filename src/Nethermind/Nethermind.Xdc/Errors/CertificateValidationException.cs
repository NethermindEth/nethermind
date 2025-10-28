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
    InvalidSignatures,
    InvalidRound,
    InvalidGapNumber

}

[Serializable]
public class CertificateValidationException(CertificateType certificateType, CertificateValidationFailure failure, Exception? innerException = null)
    : Exception($"Invalid {certificateType}: {failure}", innerException)
{
    public CertificateType CertificateType { get; } = certificateType;
    public CertificateValidationFailure Failure { get; } = failure;
}
