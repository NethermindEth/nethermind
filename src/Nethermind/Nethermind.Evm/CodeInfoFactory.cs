// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using Nethermind.Evm.EvmObjectFormat;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoFactory
{
    public static ICodeInfo CreateCodeInfo(ReadOnlyMemory<byte> code, IReleaseSpec spec, ValidationStrategy validationRules = ValidationStrategy.ExtractHeader, ValueHash256? codeHash = null)
    {
        if (spec.IsEofEnabled
            && code.Span.StartsWith(EofValidator.MAGIC)
            && EofValidator.IsValidEof(code, validationRules, out EofContainer? container))
        {
            return new EofCodeInfo(container.Value, codeHash);
        }

        CodeInfo codeInfo = new(code, codeHash);
        codeInfo.AnalyzeInBackgroundIfRequired();

        if (AotContractsRepository.TryGetIledCode(codeHash.Value, out ILEmittedMethod iledCode))
        {
            codeInfo.SetIlPrecompile(iledCode);
        }

        return codeInfo;
    }

    public static bool CreateInitCodeInfo(ReadOnlyMemory<byte> data, IReleaseSpec spec, [NotNullWhen(true)] out ICodeInfo? codeInfo, out ReadOnlyMemory<byte> extraCallData)
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
