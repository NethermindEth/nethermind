// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public class InvalidCodeException : EvmException
    {
        public override EvmExceptionType ExceptionType => EvmExceptionType.InvalidCode;
    }
}
