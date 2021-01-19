//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
