// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm
{
    public static class StatusCode
    {
        public const byte Failure = 0;
        public static readonly ReadOnlyMemory<byte> FailureBytes = Bytes.ZeroByte;
        public const byte Success = 1;
        public static readonly ReadOnlyMemory<byte> SuccessBytes = Bytes.OneByte;
    }
}
