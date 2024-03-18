// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8NTool;

public static class ExitCodes
{
    public const int ErrorEVM = 2;
    public const int ErrorConfig = 3;

    public const int ErrorMissingBlockhash = 4;

    public const int ErrorJson = 10;
    public const int ErrorIO = 11;
    public const int ErrorRlp = 12;
}
