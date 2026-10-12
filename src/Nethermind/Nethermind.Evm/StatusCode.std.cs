// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

public static partial class StatusCode
{
    public static readonly ReadOnlyMemory<byte> FailureBytes = Bytes.ZeroByte;
    public static readonly ReadOnlyMemory<byte> SuccessBytes = Bytes.OneByte;
}
