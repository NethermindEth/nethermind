// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using Nethermind.Core;

namespace Nethermind.KeyStore
{
    public interface IPasswordProvider
    {
        /// <summary>Key for the keyed registration that resolves passwords from config only (no interactive fallback).</summary>
        public const string ConfigOnly = "ConfigOnlyPasswordProvider";

        /// <summary>Key for the keyed registration that falls back to a console prompt when the password is not in config.</summary>
        public const string ConsoleFallback = "ConsoleFallbackPasswordProvider";

        SecureString GetPassword(Address address);
    }
}
