// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class UInt256Extensions
{
    // value?.IsZero == false <=> x > 0
    public static bool IsPositive(this UInt256? @this) => @this?.IsZero == false;
}
