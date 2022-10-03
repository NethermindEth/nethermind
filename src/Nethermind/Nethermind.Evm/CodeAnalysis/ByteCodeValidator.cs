using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Org.BouncyCastle.Crypto.Agreement.Srp;

namespace Nethermind.Evm.CodeAnalysis
{
    internal static class ByteCodeValidator
    {
        private static EvmObjectFormat EofFormatChecker = new EvmObjectFormat();

        public static bool HasEOFMagic(this Span<byte> code) => EofFormatChecker.HasEOFFormat(code);
        public static bool HasEOFMagic(this byte[] code) => EofFormatChecker.HasEOFFormat(code);

        public static bool ValidateByteCode(this Span<byte> code, IReleaseSpec _spec, out EofHeader header)
        {
            if(_spec.IsEip3540Enabled && code.HasEOFMagic())
            {
                 return code.IsEOFCode(out header);
            }

            header = null;
            return  !CodeDepositHandler.CodeIsInvalid(_spec, code.ToArray());
        }
        public static bool ValidateByteCode(this byte[] code, IReleaseSpec _spec, out EofHeader header)
            => code.AsSpan().ValidateByteCode(_spec, out header);

        public static bool ValidateByteCode(this byte[] code, IReleaseSpec _spec)
            => code.AsSpan().ValidateByteCode(_spec);
        public static bool ValidateByteCode(this Span<byte> code, IReleaseSpec _spec)
            => code.ValidateByteCode(_spec, out _);

        public static int CodeStartIndex(this EofHeader header)
            => EofFormatChecker.codeStartOffset(header);
        public static int CodeEndIndex(this EofHeader header)
            => EofFormatChecker.codeEndOffset(header);

        public static bool IsEOFCode(this byte[] machineCode, out EofHeader header)
            => machineCode.AsSpan().IsEOFCode(out header);
        public static bool IsEOFCode(this Span<byte> machineCode, out EofHeader header)
            => EofFormatChecker.ExtractHeader(machineCode, out header);


        public static (int, int) CodeSectionOffsets(this byte[] code)
            => EofFormatChecker.ExtractCodeOffsets(code);
        public static (int, int) CodeSectionOffsets(this Span<byte> code)
            => EofFormatChecker.ExtractCodeOffsets(code);

        public static int CodeStartIndex(this byte[] machineCode)
            => machineCode.AsSpan().CodeStartIndex();
        public static int CodeStartIndex(this Span<byte> machineCode)
            => machineCode.IsEOFCode(out var header)
                    ? header?.CodeStartIndex() ?? throw new InvalidCodeException()
                    : 0;

        public static int CodeEndIndex(this byte[] machineCode)
            => machineCode.AsSpan().CodeEndIndex();
        public static int CodeEndIndex(this Span<byte> machineCode)
            => machineCode.IsEOFCode(out var header)
                    ? header?.CodeEndIndex() ?? throw new InvalidCodeException()
                    : machineCode.Length;

        public static int CodeSize(this byte[] machineCode)
            => machineCode.AsSpan().CodeSize();
        public static int CodeSize(this Span<byte> machineCode)
            => machineCode.IsEOFCode(out var header)
                    ? header?.CodeSize ?? throw new InvalidCodeException()
                    : machineCode.Length;
    }
}
