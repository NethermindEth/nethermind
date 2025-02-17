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
    public static class EofStatusCode
    {
        public const byte Success = 0;
        public static readonly ReadOnlyMemory<byte> SuccessBytes = Bytes.ZeroByte;
        public const byte Revert = 1;
        public static readonly ReadOnlyMemory<byte> RevertBytes = Bytes.OneByte;
        public const byte Failure = 2;
        public static readonly ReadOnlyMemory<byte> FailureBytes = Bytes.TwoByte;
    }
}
