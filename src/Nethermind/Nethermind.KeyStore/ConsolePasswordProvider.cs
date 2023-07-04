// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using Nethermind.Core;
using Nethermind.KeyStore.ConsoleHelpers;

namespace Nethermind.KeyStore
{
    public class ConsolePasswordProvider : BasePasswordProvider
    {
        private readonly IConsoleUtils _consoleUtils;

        public ConsolePasswordProvider(IConsoleUtils consoleUtils)
        {
            _consoleUtils = consoleUtils;
        }

        public string Message { get; set; }
        public override SecureString GetPassword(Address address)
        {
            var password = _consoleUtils.ReadSecret(Message);
            if (password is null && AlternativeProvider is not null)
                password = AlternativeProvider.GetPassword(address);

            return password;
        }
    }
}
