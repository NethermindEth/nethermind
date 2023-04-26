// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public static bool CodeIsValid(ReadOnlySpan<byte> code, IReleaseSpec spec, int fromVersion)
        {
            bool valid = true;
            if (spec.IsEip3540Enabled)
            {
                //fromVersion = (execType is ExecutionType.Create1 or ExecutionType.Create2) ? fromVersion : 0; //// hmmmm
                valid = IsValidWithEofRules(code, fromVersion);
            }
            else if (spec.IsEip3541Enabled)
            {
                valid = IsValidWithLegacyRules(code);
            }

            return valid;
        }

        public static bool IsValidWithLegacyRules(ReadOnlySpan<byte> code)
        {
            return code is not [InvalidStartingCodeByte, ..]; ;
        }

        public static bool IsValidWithEofRules(ReadOnlySpan<byte> code, int fromVersion)
        {
            bool isCodeEof = EvmObjectFormat.IsEof(code);
            int codeVersion = isCodeEof ? EvmObjectFormat.GetCodeVersion(code) : 0;
            bool valid = codeVersion >= fromVersion
                  && (isCodeEof ?  // this needs test cases
                       EvmObjectFormat.IsValidEof(code, out _) :
                            fromVersion > 0 ? false : code is not [InvalidStartingCodeByte, ..]);
            return valid;
        }

        public static bool CodeIsInvalid(ReadOnlySpan<byte> code, IReleaseSpec spec, int fromVersion) => !CodeIsValid(code, spec, fromVersion);

        public static bool CreateCodeIsValid(ICodeInfo codeInfo, ReadOnlySpan<byte> initCode, IReleaseSpec spec)
        {
            if (spec.IsEip3540Enabled)
            {
                byte containerVersion = codeInfo.EofVersion();
                return CodeIsValid(initCode, spec, containerVersion);
            }
            return true;
        }
    }
}
