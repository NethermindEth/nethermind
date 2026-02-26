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

        public static long CalculateCost(in SpecSnapshot spec, int byteCodeLength) =>
            spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize
                ? long.MaxValue
                : GasCostOf.CodeDeposit * byteCodeLength;

        public static long CalculateCost(IReleaseSpec spec, int byteCodeLength) =>
            spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize
                ? long.MaxValue
                : GasCostOf.CodeDeposit * byteCodeLength;

        public static bool CodeIsInvalid(in SpecSnapshot spec, ReadOnlyMemory<byte> code, int fromVersion)
            => !CodeIsValid(in spec, code, fromVersion);

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code, int fromVersion)
            => !CodeIsValid(spec, code, fromVersion);

        public static bool CodeIsValid(in SpecSnapshot spec, ReadOnlyMemory<byte> code, int fromVersion)
            => spec.IsEofEnabled ? IsValidWithEofRules(in spec, code, fromVersion) : IsValidWithLegacyRules(in spec, code);

        public static bool CodeIsValid(IReleaseSpec spec, ReadOnlyMemory<byte> code, int fromVersion)
            => spec.IsEofEnabled ? IsValidWithEofRules(spec, code, fromVersion) : IsValidWithLegacyRules(spec, code);

        public static bool IsValidWithLegacyRules(in SpecSnapshot spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);

        public static bool IsValidWithLegacyRules(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);

        public static bool IsValidWithEofRules(in SpecSnapshot spec, ReadOnlyMemory<byte> code, int fromVersion, ValidationStrategy strategy = ValidationStrategy.Validate)
        {
            byte codeVersion = 0;
            bool isCodeEof = code.Length >= EofValidator.MAGIC.Length && EofValidator.IsEof(code, out codeVersion);
            bool valid = codeVersion >= fromVersion
                  && (isCodeEof
                        ? (fromVersion > 0 && EofValidator.IsValidEof(code, strategy, out _))
                        : (fromVersion == 0 && IsValidWithLegacyRules(in spec, code)));
            return valid;
        }

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
