// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm.EvmObjectFormat;

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
            => spec.IsEofEnabled ? IsValidWithEofRules(spec, code, fromVersion) : IsValidWithLegacyRules(spec, code);

        public static bool IsValidWithLegacyRules(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);

        public static bool IsValidWithEofRules(IReleaseSpec spec, ReadOnlyMemory<byte> code, int fromVersion, ValidationStrategy strategy = ValidationStrategy.Validate)
        {
            byte codeVersion = 0;
            bool isCodeEof = code.Length >= EofValidator.MAGIC.Length && EofValidator.IsEof(code, out codeVersion);
            bool valid = codeVersion >= fromVersion
                  && (isCodeEof
                        ? (fromVersion > 0 && EofValidator.IsValidEof(code, strategy, out _))
                        : (fromVersion == 0 && IsValidWithLegacyRules(spec, code)));
            return valid;
        }
    }
}
