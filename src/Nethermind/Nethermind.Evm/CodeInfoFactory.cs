// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Specs;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static ICodeInfo CreateCodeInfo(ReadOnlyMemory<byte> code, IReleaseSpec spec, ValidationStrategy validationRules = ValidationStrategy.ExtractHeader)
    {
        if (spec.IsEofEnabled
            && code.Span.StartsWith(EofValidator.MAGIC)
            && EofValidator.IsValidEof(code, validationRules, out EofContainer? container))
        {
            return new EofCodeInfo(container.Value);
        }
        CodeInfo codeInfo = new(code);
        codeInfo.AnalyzeInBackgroundIfRequired();
        return codeInfo;
    }

    public static bool CreateInitCodeInfo(Memory<byte> data, IReleaseSpec spec, [NotNullWhen(true)] out ICodeInfo? codeInfo, out Memory<byte> extraCallData)
    {
        extraCallData = default;
        if (spec.IsEofEnabled && data.Span.StartsWith(EofValidator.MAGIC))
        {
            if (EofValidator.IsValidEof(data, ValidationStrategy.ValidateInitCodeMode | ValidationStrategy.ValidateFullBody | ValidationStrategy.AllowTrailingBytes, out EofContainer? eofContainer))
            {
                int containerSize = eofContainer.Value.Header.DataSection.EndOffset;
                extraCallData = data[containerSize..];
                codeInfo = new EofCodeInfo(eofContainer.Value);
                return true;
            }
            codeInfo = null;
            return false;
        }

        CodeInfo legacyCodeInfo = new(data);
        legacyCodeInfo.AnalyzeInBackgroundIfRequired();
        codeInfo = legacyCodeInfo;
        return true;
    }
}
