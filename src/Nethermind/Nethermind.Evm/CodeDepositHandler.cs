// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;
        public static long CalculateCost(IReleaseSpec spec, int byteCodeLength)
        {
            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
                return long.MaxValue;

            return GasCostOf.CodeDeposit * byteCodeLength;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code, int fromVersion)
            => !CodeIsValid(spec, code, fromVersion);

        public static bool CodeIsValid(IReleaseSpec spec, ReadOnlyMemory<byte> code, int fromVersion)
            => spec.IsEofEnabled ? IsValidWithEofRules(spec, code.Span, fromVersion) : IsValidWithLegacyRules(spec, code.Span);

        public static bool IsValidWithLegacyRules(IReleaseSpec spec, ReadOnlySpan<byte> code)
            => !spec.IsEip3541Enabled || code is not [InvalidStartingCodeByte, ..];

        public static bool IsValidWithEofRules(IReleaseSpec spec, ReadOnlySpan<byte> code, int fromVersion, EvmObjectFormat.ValidationStrategy strategy = EvmObjectFormat.ValidationStrategy.Validate)
        {
            bool isCodeEof = EvmObjectFormat.IsEof(code, out int codeVersion);
            bool valid = code.Length >= 1
                  && codeVersion >= fromVersion
                  && (isCodeEof
                        ? EvmObjectFormat.IsValidEof(code, strategy, out _)
                        : (fromVersion > 0 ? false : IsValidWithLegacyRules(spec, code)));
            return valid;
        }
    }
}
