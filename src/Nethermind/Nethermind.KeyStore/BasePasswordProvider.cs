// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using Nethermind.Core;
using Nethermind.KeyStore.ConsoleHelpers;

namespace Nethermind.KeyStore
{
    public abstract class BasePasswordProvider : IPasswordProvider
    {
        public IPasswordProvider AlternativeProvider { get; private set; }

        public BasePasswordProvider OrReadFromConsole(string message)
        {
            ConsoleUtils consoleUtils = new ConsoleUtils(new ConsoleWrapper());
            AlternativeProvider = new ConsolePasswordProvider(consoleUtils) { Message = message };
            return this;
        }

        public BasePasswordProvider OrReadFromFile(string fileName)
        {
            AlternativeProvider = new FilePasswordProvider(address => fileName);
            return this;
        }

        public abstract SecureString GetPassword(Address address);
    }
}
