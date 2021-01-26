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
