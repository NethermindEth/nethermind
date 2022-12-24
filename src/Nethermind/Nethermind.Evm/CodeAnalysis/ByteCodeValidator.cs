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

        private readonly EvmObjectFormat? EofFormatChecker;
        public ByteCodeValidator(ILogManager logManager = null)
            => EofFormatChecker = new EvmObjectFormat(logManager);

        public bool HasEofPrefix(ReadOnlySpan<byte> code) => EofFormatChecker.IsEof(code);
        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec spec,
            [NotNullWhen(true)] out EofHeader? header)
        {
            header = null;
            return !spec.IsEip3540Enabled || !EofFormatChecker.IsEof(code)
                ? CodeDepositHandler.CodeIsValid(spec, code.ToArray())
                : EofFormatChecker.TryExtractEofHeader(code, out header);
        }

        public bool ValidateBytecode(ReadOnlySpan<byte> code, IReleaseSpec _spec)
            => ValidateBytecode(code, _spec, out _);

        public bool ValidateEofBytecode(ReadOnlySpan<byte> code,
            [NotNullWhen(true)] out EofHeader? header)
            => EofFormatChecker.TryExtractEofHeader(code, out header);

    }
}
