// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public class InvalidJumpDestinationException : EvmException
    {
        public override EvmExceptionType ExceptionType => EvmExceptionType.InvalidJumpDestination;
    }
}
