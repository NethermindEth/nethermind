// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

internal sealed class SecureStringWrapper : IDisposable
{
    private bool _disposed = false;
    public SecureString SecureData { get; private set; }

    public SecureStringWrapper(string data)
    {
        SecureData = CreateSecureString(data);
    }

    private SecureString CreateSecureString(string regularString)
    {
        var secureString = new SecureString();
        foreach (char c in regularString)
        {
            secureString.AppendChar(c);
        }
        secureString.MakeReadOnly();
        return secureString;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                SecureData.Dispose();
            }
            _disposed = true;
        }
    }

    ~SecureStringWrapper()
    {
        Dispose(false);
    }
}
