// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core.Exceptions;

namespace Nethermind.HealthChecks;
public class NotEnoughDiskSpaceException : Exception, IExceptionWithExitCode
{
    public int ExitCode => ExitCodes.LowDiskSpace;
}
