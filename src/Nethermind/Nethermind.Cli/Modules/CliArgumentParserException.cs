// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Modules
{
    public class CliArgumentParserException : Exception
    {
        public CliArgumentParserException(string message) : base(message) { }
    }
}
