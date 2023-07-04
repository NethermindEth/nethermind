// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network
{
    public enum NetworkExceptionType
    {
        TargetUnreachable,
        Timeout,
        Validation,
        Discovery,
        HandshakeOrInit,
        Other
    }
}
