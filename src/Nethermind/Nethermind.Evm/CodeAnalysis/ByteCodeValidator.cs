using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Org.BouncyCastle.Crypto.Agreement.Srp;

namespace Nethermind.Evm.CodeAnalysis
{
    internal static class ByteCodeValidator
    {
        private static EvmObjectFormat? EofFormatChecker = new EvmObjectFormat();
        public static void Initialize(ILogger logger = null)
        {
            if (EofFormatChecker is not null)
                EofFormatChecker = new EvmObjectFormat(logger);
        }

        public static bool HasEOFMagic(this ReadOnlySpan<byte> code) => EofFormatChecker.HasEOFFormat(code);
        public static bool ValidateByteCode(ReadOnlySpan<byte> code, IReleaseSpec _spec, out EofHeader header)
        {
            if (_spec.IsEip3670Enabled && code.HasEOFMagic())
            {
                return EofFormatChecker.ValidateInstructions(code, out header);
            }
            else if (_spec.IsEip3540Enabled && code.HasEOFMagic())
            {
                return IsEOFCode(code, out header);
            }

            header = null;
            return !CodeDepositHandler.CodeIsInvalid(_spec, code.ToArray());
        }
        public static bool ValidateByteCode(this ReadOnlySpan<byte> code, IReleaseSpec _spec)
                => ValidateByteCode(code, _spec, out _);

        public static bool IsEOFCode(ReadOnlySpan<byte> machineCode, out EofHeader header)
            => EofFormatChecker.ExtractHeader(machineCode, out header);
        public static int CodeStartIndex(ReadOnlySpan<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeStartOffset
                    : 0;
        public static int CodeEndIndex(ReadOnlySpan<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeEndOffset
                    : machineCode.Length;

        public static int CodeSize(ReadOnlySpan<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeSize
                    : machineCode.Length;
    }
}
