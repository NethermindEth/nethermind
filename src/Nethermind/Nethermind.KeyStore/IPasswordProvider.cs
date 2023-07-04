// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using Nethermind.Core;

namespace Nethermind.KeyStore
{
    public interface IPasswordProvider
    {
        SecureString GetPassword(Address address);
    }
}
