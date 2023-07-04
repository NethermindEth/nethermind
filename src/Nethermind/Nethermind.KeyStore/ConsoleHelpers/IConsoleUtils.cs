// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;

namespace Nethermind.KeyStore.ConsoleHelpers
{
    public interface IConsoleUtils
    {
        SecureString ReadSecret(string secretDisplayName);
    }
}
