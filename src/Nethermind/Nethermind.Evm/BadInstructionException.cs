// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public class BadInstructionException : EvmException
{
    public override EvmExceptionType ExceptionType => EvmExceptionType.BadInstruction;
}
