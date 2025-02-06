// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.EvmObjectFormat;

public enum ValidationStrategy : byte
{
    None = 0,
    Validate = 1,
    ValidateFullBody = Validate | 2,
    ValidateInitCodeMode = Validate | 4,
    ValidateRuntimeMode = Validate | 8,
    AllowTrailingBytes = Validate | 16,
    ExractHeader = 32,
    HasEofMagic = 64,

}
