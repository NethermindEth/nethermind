// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static CodeInfo CreateCodeInfo(ReadOnlyMemory<byte> code)
    {
        CodeInfo codeInfo = new(code);
        codeInfo.AnalyzeInBackgroundIfRequired();
        return codeInfo;
    }
}
