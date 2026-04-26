// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

public class InvalidConfigurationException(string message, int exitCode) : Exception(message), IExceptionWithExitCode
{
    public int ExitCode { get; } = exitCode;
}
