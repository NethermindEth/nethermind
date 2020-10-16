//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.IO;
using System.Security;
using Nethermind.Crypto;

namespace Nethermind.KeyStore
{
    

    public abstract class BasePasswordProvider : IPasswordProvider
    {
        protected IPasswordProvider _alternativeProvider;

        private void SetSuccessor(IPasswordProvider alternativeProvider)
        {
            _alternativeProvider = alternativeProvider;
        }

        public IPasswordProvider OrReadFromConsole(string message)
        {
            SetSuccessor(new ConsolePasswordProvider() { Message = message });
            return this;
        }

        public abstract SecureString GetPassword();

        public SecureString GetPasswordFromConsole(string message)
        {
            return ConsoleUtils.ReadSecret(message);
        }
    }
}
