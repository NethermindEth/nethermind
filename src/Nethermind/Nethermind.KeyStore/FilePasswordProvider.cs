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
using System.Collections.Generic;
using System.IO;
using System.Security;
using Nethermind.Core;

namespace Nethermind.KeyStore
{
    public class FilePasswordProvider : BasePasswordProvider
    {
        private readonly Func<Address, string> _addressToFileMapper;

        public FilePasswordProvider(Func<Address, string> addressToFileMapper)
        {
            _addressToFileMapper = addressToFileMapper ?? throw new ArgumentNullException(nameof(addressToFileMapper));
        }
        
        public override SecureString GetPassword(Address address)
        {
            string fileName = _addressToFileMapper(address);
            SecureString password = null;
            if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
            {
                password = GetPasswordFromFile(fileName);
            }

            if (password == null && AlternativeProvider != null)
                password = AlternativeProvider.GetPassword(address);

            return password;
        }

        public static SecureString GetPasswordFromFile(string filePath)
        {
            var whitespaces = new List<char>();
            var secureString = new SecureString();
            using (StreamReader stream = new StreamReader(filePath))
            {
                bool trimBeginFinished = false;
                while (stream.Peek() >= 0)
                {
                    var character = (char)stream.Read();
                    if (char.IsWhiteSpace(character))
                    {
                        if (trimBeginFinished)
                        {
                            whitespaces.Add(character);
                        }
                    }
                    else
                    {
                        trimBeginFinished = true;
                        if (whitespaces.Count != 0)
                        {
                            FillWhitespaceList(secureString, whitespaces);
                            whitespaces.Clear();
                        }

                        secureString.AppendChar(character);
                    }
                }
            }

            secureString.MakeReadOnly();
            return secureString;
        }

        private static void FillWhitespaceList(SecureString secureString, List<char> whitespaces)
        {
            foreach (char whitespace in whitespaces)
            {
                secureString.AppendChar(whitespace);
            }
        }
    }
}
