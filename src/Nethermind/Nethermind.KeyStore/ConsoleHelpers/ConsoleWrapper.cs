// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.KeyStore.ConsoleHelpers
{
    public class ConsoleWrapper : IConsoleWrapper
    {
        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            return Console.ReadKey(intercept);
        }

        public void Write(string message)
        {
            Console.Write(message);
        }

        public void WriteLine(string message = null)
        {
            Console.WriteLine(message);
        }
    }
}
