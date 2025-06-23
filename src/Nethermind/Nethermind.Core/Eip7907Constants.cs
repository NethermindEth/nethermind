// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7907Constants
{
    public const int MaxCodeSize = 24_576; // 24KiB
    public const int MaxCodeSizeEip7907 = 262_144; // 256KiB

    public const int MaxInitCodeSize = MaxCodeSize * 2; // 48KiB
    public const int MaxInitCodeSizeEip7907 = MaxCodeSizeEip7907 * 2; // 512KiB
}
