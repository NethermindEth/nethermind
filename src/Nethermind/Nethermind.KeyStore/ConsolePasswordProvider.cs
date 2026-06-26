// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using Nethermind.Core;
using Nethermind.KeyStore.ConsoleHelpers;

namespace Nethermind.KeyStore
{
    public class ConsolePasswordProvider(IConsoleUtils consoleUtils) : BasePasswordProvider
    {
        private readonly IConsoleUtils _consoleUtils = consoleUtils;

        public string Message { get; set; } = string.Empty;
        public override SecureString GetPassword(Address address) => _consoleUtils.ReadSecret(Message);
    }
}
