// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZKVM
namespace Nethermind.Core;

public static partial class Flag
{
    public static partial bool IsActive<TFlag>() where TFlag : struct, IFlag => typeof(TFlag) == typeof(OnFlag);
}
#endif
