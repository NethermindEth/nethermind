// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1.Exceptions;
public class EraVerificationException : Exception
{
    public EraVerificationException()
    {
    }

    public EraVerificationException(string message) : base(message)
    {
    }

    public EraVerificationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
