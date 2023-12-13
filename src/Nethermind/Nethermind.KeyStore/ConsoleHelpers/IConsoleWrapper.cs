// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.KeyStore.ConsoleHelpers
{
    public interface IConsoleWrapper
    {
        void WriteLine(string message = null);

        ConsoleKeyInfo ReadKey(bool intercept);

        void Write(string message);
    }
}
