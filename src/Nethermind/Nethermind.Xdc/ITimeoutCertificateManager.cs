
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public interface ITimeoutCertificateManager
{
    Task OnReceiveTimeout(Timeout timeout);
    Task HandleTimeout(Timeout timeout);
    void OnCountdownTimer();
    void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate);
    bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, out string errorMessage);
}
