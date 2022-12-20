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

        public bool HasEofPrefix(ReadOnlySpan<byte> code,
            [NotNullWhen(true)] out byte? version) => EofFormatChecker.HasEofFormat(code, out version);
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec,
            [NotNullWhen(true)] out EofHeader? header)
        {
            if (_spec.IsEip3540Enabled)
            {
                if (EofFormatChecker.ValidateEofCode(_spec, code, out header))
                {
                    return true;
                }
            }

            header = null;
            return CodeDepositHandler.CodeIsValid(_spec, code.ToArray());
        }
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec)
            => ValidateBytecode(code, _spec, out _);

        public bool ValidateEofBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec,
            [NotNullWhen(true)] out EofHeader? header)
            => EofFormatChecker.ValidateEofCode(_spec, code, out header);
        public bool ValidateEofBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec)
            => ValidateEofBytecode(code, _spec, out _);
    }
}
