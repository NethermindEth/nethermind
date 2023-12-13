// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (password is null && AlternativeProvider is not null)
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
