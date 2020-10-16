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

namespace Nethermind.KeyStore
{
    public class FilePasswordProvider : BasePasswordProvider, IPasswordProvider
    {
        public string FileName { get; set; }
        public override SecureString GetPassword()
        {
            if (string.IsNullOrWhiteSpace(FileName) || !File.Exists(FileName))
            {
                return null;
            }

            var password = GetPasswordFromFile(FileName);
            if (password == null)
                password = _alternativeProvider.GetPassword();

            return password;
        }

        public SecureString GetPasswordFromFile(string filePath)
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

        private void FillWhitespaceList(SecureString secureString, List<char> whitespaces)
        {
            foreach (var whitespace in whitespaces)
            {
                secureString.AppendChar(whitespace);
            }
        }
    }
}
