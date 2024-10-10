// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Serialization;

namespace Nethermind.Blockchain;
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
