using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Nethermind.Specs;

namespace Nethermind.Evm.CodeAnalysis
{
    internal class ByteCodeValidator
    {
        public static ByteCodeValidator Instance => new ByteCodeValidator();

        private EvmObjectFormat? EofFormatChecker = new EvmObjectFormat();
        public ByteCodeValidator(ILogManager loggerManager = null)
            => EofFormatChecker = new EvmObjectFormat(loggerManager);

        public bool HasEOFMagic(ReadOnlySpan<byte> code) => EofFormatChecker.HasEOFFormat(code);
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec, out EofHeader header)
        {
            if (_spec.IsEip3540Enabled && HasEOFMagic(code))
            {
                return EofFormatChecker.ValidateInstructions(code, out header, _spec);
            }

            header = null;
            return !CodeDepositHandler.CodeIsInvalid(_spec, code.ToArray());
        }
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec)
                => ValidateBytecode(code, _spec, out _);

        public bool ValidateEofStructure(ReadOnlySpan<byte> machineCode, IReleaseSpec _spec, out EofHeader header)
            => EofFormatChecker.ValidateInstructions(machineCode, out header, _spec);
    }
}
