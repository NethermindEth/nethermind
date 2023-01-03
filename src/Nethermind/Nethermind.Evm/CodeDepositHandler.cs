// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;

        public static long CalculateCost(int byteCodeLength, IReleaseSpec spec)
        {
            return spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize
                ? long.MaxValue
                : GasCostOf.CodeDeposit * byteCodeLength;
        }

        public static bool CodeIsValid(ReadOnlySpan<byte> code, IReleaseSpec spec)
        {
            return spec.IsEip3540Enabled && EvmObjectFormat.IsEof(code)
                ? EvmObjectFormat.TryExtractHeader(code, out _)
                : !spec.IsEip3541Enabled || code is not [InvalidStartingCodeByte, ..];
        }

        public static bool CodeIsInvalid(ReadOnlySpan<byte> code, IReleaseSpec spec) => !CodeIsValid(code, spec);

        public static bool IsInitCodeOrReturnCodeValid(ICodeInfo codeInfo, ReadOnlySpan<byte> initCode, IReleaseSpec spec)
        {
            if (spec.IsEip3540Enabled)
            {
                byte version = codeInfo.EofVersion();
                if (version > 0 && version != EvmObjectFormat.GetCodeVersion(initCode))
                    return false;
                if (EvmObjectFormat.IsEof(initCode)) // this needs test cases
                    return EvmObjectFormat.IsValidEof(initCode, out _);
            }

            return true;
        }
    }
}
