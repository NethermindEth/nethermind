// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class StatusCode
    {
        public const byte Failure = 0;
        public static readonly byte[] FailureBytes = new byte[] { 0 };
        public const byte Success = 1;
        public static readonly byte[] SuccessBytes = new byte[] { 1 };
    }
}
