using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm.CodeAnalysis
{
    internal class ByteCodeValidator
    {
        public static ByteCodeValidator Instance => new ByteCodeValidator();

        private EvmObjectFormat? EofFormatChecker = new EvmObjectFormat();
        public ByteCodeValidator(ILogManager logManager = null)
            => EofFormatChecker = new EvmObjectFormat(logManager);

        public bool HasEOFMagic(ReadOnlySpan<byte> code) => EofFormatChecker.HasEofFormat(code);
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec,
            [NotNullWhen(true)] out EofHeader? header)
        {
            if (_spec.IsEip3540Enabled)
            {
                if( EofFormatChecker.ExtractHeader(code, _spec, out header)
                    && EofFormatChecker.ValidateInstructions(code, _spec, in header))
                {
                    return true;
                }
            }

            header = null;
            return CodeDepositHandler.CodeIsValid(_spec, code.ToArray());
        }
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec)
                => ValidateBytecode(code, _spec, out _);

        public bool ValidateHeader(ReadOnlySpan<byte> machineCode, IReleaseSpec _spec, out EofHeader? header)
            => EofFormatChecker.ExtractHeader(machineCode, _spec, out header);
        public bool ValidateEofStructure(ReadOnlySpan<byte> machineCode, IReleaseSpec _spec, EofHeader? header)
            => EofFormatChecker.ValidateInstructions(machineCode, _spec, header);
    }
}
