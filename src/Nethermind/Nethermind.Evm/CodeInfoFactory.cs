// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.EvmObjectFormat;
using System;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static ICodeInfo CreateCodeInfo(ReadOnlyMemory<byte> code, IReleaseSpec spec, EvmObjectFormat.ValidationStrategy validationRules = EvmObjectFormat.ValidationStrategy.ExractHeader)
    {
        CodeInfo codeInfo = new CodeInfo(code);
        if (spec.IsEofEnabled && code.Span.StartsWith(EofValidator.MAGIC))
        {
            if (EofValidator.IsValidEof(code, validationRules, out EofContainer? container))
            {
                return new EofCodeInfo(container.Value);
            }
        }
        codeInfo.AnalyseInBackgroundIfRequired();
        return codeInfo;
    }

    public static bool CreateInitCodeInfo(Memory<byte> data, IReleaseSpec spec, out ICodeInfo codeInfo, out Memory<byte> extraCalldata)
    {
        extraCalldata = default;
        if (spec.IsEofEnabled && data.Span.StartsWith(EofValidator.MAGIC))
        {
            if (EofValidator.IsValidEof(data, EvmObjectFormat.ValidationStrategy.ValidateInitcodeMode | EvmObjectFormat.ValidationStrategy.ValidateFullBody | EvmObjectFormat.ValidationStrategy.AllowTrailingBytes, out EofContainer? eofContainer))
            {
                int containerSize = eofContainer.Value.Header.DataSection.EndOffset;
                extraCalldata = data.Slice(containerSize);
                ICodeInfo innerCodeInfo = new CodeInfo(data.Slice(0, containerSize));
                codeInfo = new EofCodeInfo(eofContainer.Value);
                return true;
            }
            codeInfo = null;
            return false;
        }
        codeInfo = new CodeInfo(data);
        codeInfo.AnalyseInBackgroundIfRequired();
        return true;
    }
}
