// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Console
{
    public interface ICliConsole
    {
        void WriteException(Exception e);

        void WriteErrorLine(string errorMessage);

        void WriteLine(object objectToWrite);

        void Write(object objectToWrite);

        void WriteCommentLine(object objectToWrite);

        void WriteLessImportant(object objectToWrite);

        void WriteKeyword(string keyword);

        void WriteInteresting(string interesting);

        void WriteLine();

        void WriteGood(string goodText);

        void WriteString(object result);

        void ResetColor();

        Terminal Terminal { get; }
    }
}
