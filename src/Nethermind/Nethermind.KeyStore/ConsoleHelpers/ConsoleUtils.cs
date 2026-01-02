// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;

namespace Nethermind.KeyStore.ConsoleHelpers
{
    public class ConsoleUtils : IConsoleUtils
    {
        private readonly IConsoleWrapper _consoleWrapper;

        public ConsoleUtils(IConsoleWrapper consoleWrapper)
        {
            _consoleWrapper = consoleWrapper;
        }

        public SecureString ReadSecret(string secretDisplayName)
        {
            _consoleWrapper.WriteLine($"{secretDisplayName}:");
            SecureString secureString = new SecureString();
            do
            {
                ConsoleKeyInfo key = _consoleWrapper.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secureString.Length > 0)
                    {
                        secureString.RemoveAt(secureString.Length - 1);
                        _consoleWrapper.Write("\b \b");
                    }

                    continue;
                }

                secureString.AppendChar(key.KeyChar);
                _consoleWrapper.Write("*");
            } while (true);

            _consoleWrapper.WriteLine();

            secureString.MakeReadOnly();
            return secureString;
        }
    }
}
