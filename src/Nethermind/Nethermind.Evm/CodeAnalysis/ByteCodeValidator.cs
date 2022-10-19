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
            if (EofFormatChecker is null)
                EofFormatChecker = new EvmObjectFormat(logger);
        }

        public static bool HasEOFMagic(this Span<byte> code) => EofFormatChecker.HasEOFFormat(code);
        public static bool ValidateByteCode(Span<byte> code, IReleaseSpec _spec, out EofHeader header)
        {
            if (_spec.IsEip3540Enabled && code.HasEOFMagic())
            {
                return IsEOFCode(code, out header);
            }

            header = null;
            return !CodeDepositHandler.CodeIsInvalid(_spec, code.ToArray());
        }
        public static bool ValidateByteCode(this Span<byte> code, IReleaseSpec _spec)
            => ValidateByteCode(code, _spec, out _);

        public static bool IsEOFCode(Span<byte> machineCode, out EofHeader header)
            => EofFormatChecker.ExtractHeader(machineCode, out header);
        public static int CodeStartIndex(Span<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeStartOffset
                    : 0;
        public static int CodeEndIndex(Span<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeEndOffset
                    : machineCode.Length;

        public static int CodeSize(Span<byte> machineCode)
            => IsEOFCode(machineCode, out var header)
                    ? header.CodeSize
                    : machineCode.Length;
    }
}
