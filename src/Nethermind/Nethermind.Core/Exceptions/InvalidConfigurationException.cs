// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

public class InvalidConfigurationException : Exception, IExceptionWithExitCode
{
    public InvalidConfigurationException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
