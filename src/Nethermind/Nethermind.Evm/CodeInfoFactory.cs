// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static ICodeInfo CreateCodeInfo(byte[] code, IReleaseSpec spec)
    {
        CodeInfo codeInfo = new(code);
        return spec.IsEofEnabled && EvmObjectFormat.IsValidEof(code, out EofHeader? header)
            ? new EofCodeInfo(codeInfo, header.Value)
            : codeInfo;
    }
}
