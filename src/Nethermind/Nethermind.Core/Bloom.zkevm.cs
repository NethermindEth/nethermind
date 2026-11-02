// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public partial class Bloom
{
    public static Bloom ZeroLength => field ??= CreateZeroLength();
}
