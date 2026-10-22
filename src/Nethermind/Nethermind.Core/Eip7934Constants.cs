// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7934Constants
{
    // MaxRlpBlockSize
    // = MaxBlockSize - SafetyMargin
    // = 10MiB - 2MiB
    // = 10_485_760 - 2_097_152 
    public const int DefaultMaxRlpBlockSize = 8_388_608;
}
