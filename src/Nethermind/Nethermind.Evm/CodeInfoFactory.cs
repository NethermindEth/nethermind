// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;
using System;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static bool CreateCodeInfo(ReadOnlyMemory<byte> code, IReleaseSpec spec, out ICodeInfo codeinfo, EvmObjectFormat.ValidationStrategy validationRules = EvmObjectFormat.ValidationStrategy.Validate)
    {
        codeinfo = new CodeInfo(code);
        if (spec.IsEofEnabled && code.Span.StartsWith(EvmObjectFormat.MAGIC))
        {
            if(EvmObjectFormat.IsValidEof(code.Span, validationRules, out EofHeader? header))
            {
                codeinfo = new EofCodeInfo(codeinfo, header.Value);
                return true;
            }
            return false;
        }
        return true;
    }

    public static bool CreateInitCodeInfo(Memory<byte> data, IReleaseSpec spec, out ICodeInfo codeinfo, out Memory<byte> extraCalldata)
    {
        codeinfo = new CodeInfo(data);
        extraCalldata = default;
        if(spec.IsEofEnabled && data.Span.StartsWith(EvmObjectFormat.MAGIC))
        {
            if(EvmObjectFormat.IsValidEof(data.Span, EvmObjectFormat.ValidationStrategy.ValidateInitcodeMode | EvmObjectFormat.ValidationStrategy.ValidateFullBody | EvmObjectFormat.ValidationStrategy.ValidateSubContainers | EvmObjectFormat.ValidationStrategy.AllowTrailingBytes, out EofHeader? header))
            {
                int containerSize = header.Value.DataSection.EndOffset;
                extraCalldata = data.Slice(containerSize);
                return true;
            }
            return false;
        }
        return true;   
    }
}
