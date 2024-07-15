// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;
using System;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static ICodeInfo CreateCodeInfo(ReadOnlyMemory<byte> code, IReleaseSpec spec, EvmObjectFormat.ValidationStrategy validationRules = EvmObjectFormat.ValidationStrategy.ExractHeader)
    {
        CodeInfo codeInfo = new CodeInfo(code);
        if (spec.IsEofEnabled && code.Span.StartsWith(EvmObjectFormat.MAGIC))
        {
            if (EvmObjectFormat.IsValidEof(code.Span, validationRules, out EofHeader? header))
            {
                return new EofCodeInfo(codeInfo, header.Value);
            }
        }
        codeInfo.AnalyseInBackgroundIfRequired();
        return codeInfo;
    }

    public static bool CreateInitCodeInfo(Memory<byte> data, IReleaseSpec spec, out ICodeInfo codeInfo, out Memory<byte> extraCalldata)
    {
        codeInfo = new CodeInfo(data);
        extraCalldata = default;
        if (spec.IsEofEnabled && data.Span.StartsWith(EvmObjectFormat.MAGIC))
        {
            if (EvmObjectFormat.IsValidEof(data.Span, EvmObjectFormat.ValidationStrategy.ValidateInitcodeMode | EvmObjectFormat.ValidationStrategy.ValidateFullBody | EvmObjectFormat.ValidationStrategy.ValidateSubContainers | EvmObjectFormat.ValidationStrategy.AllowTrailingBytes, out EofHeader? header))
            {
                int containerSize = header.Value.DataSection.EndOffset;
                extraCalldata = data.Slice(containerSize);
                codeInfo = new EofCodeInfo(codeInfo, header.Value);
                return true;
            }
            return false;
        }
        codeInfo.AnalyseInBackgroundIfRequired();
        return true;
    }
}
