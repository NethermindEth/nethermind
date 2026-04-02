
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface ITimeoutCertificateManager
{
    Task OnReceiveTimeout(Timeout timeout);
    Task HandleTimeoutVote(Timeout timeout);
    void OnCountdownTimer();
    void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate);
    bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, out string errorMessage);
    long GetTimeoutsCount(Timeout timeout);
    IDictionary<(ulong Round, Hash256 Hash), ArrayPoolList<Timeout>> GetReceivedTimeouts();
}
