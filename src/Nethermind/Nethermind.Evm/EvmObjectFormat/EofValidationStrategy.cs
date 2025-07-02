// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.EvmObjectFormat;

[Flags]
public enum ValidationStrategy : byte
{
    None = 0,
    Validate = 1,
    FullBody = 2,
    InitCodeMode = 4,
    RuntimeMode = 8,
    AllowTrailingBytes = 16,
    ValidateFullBody = Validate | FullBody,
    ValidateInitCodeMode = Validate | InitCodeMode,
    ValidateRuntimeMode = Validate | RuntimeMode,
    ExtractHeader = 32,
    HasEofMagic = 64,
}
