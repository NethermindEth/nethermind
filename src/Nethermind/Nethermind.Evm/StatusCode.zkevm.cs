// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

// Computed on access, so the guest runs no class constructor for this type.
public static partial class StatusCode
{
    public static ReadOnlyMemory<byte> FailureBytes => Bytes.ZeroByte;
    public static ReadOnlyMemory<byte> SuccessBytes => Bytes.OneByte;
}
