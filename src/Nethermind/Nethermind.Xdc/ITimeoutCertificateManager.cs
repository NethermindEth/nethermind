
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;
public interface ITimeoutCertificateManager
{
    void HandleTimeout(Timeout timeout);
    void OnCountdownTimer(DateTime time);
    void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate);
    bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, out string errorMessage);
}
