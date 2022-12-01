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
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec, out EofHeader header, bool skipEip3541 = true)
        {
            if (_spec.IsEip3540Enabled && HasEOFMagic(code))
            {
                return EofFormatChecker.ValidateEofCode(code, _spec, out header);
            }

            header = null;
            return !skipEip3541 && !CodeDepositHandler.CodeIsInvalid(_spec, code.ToArray());
        }
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec, bool skipEip3541 = false)
                => ValidateBytecode(code, _spec, out _, skipEip3541);
    }
}
