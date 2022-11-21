// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Security;

namespace Nethermind.Crypto
{
    public static class SecureStringExtensions
    {
        public static byte[] ToByteArray(this SecureString secureString, System.Text.Encoding encoding = null)
        {
            if (secureString is null)
            {
                throw new ArgumentNullException(nameof(secureString));
            }

            encoding ??= System.Text.Encoding.UTF8;

            IntPtr unmanagedString = IntPtr.Zero;
            try
            {
                unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(secureString);
                return encoding.GetBytes(Marshal.PtrToStringUni(unmanagedString) ?? throw new InvalidOperationException($"{nameof(secureString)} is null"));
            }
            finally
            {
                if (unmanagedString != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
                }
            }
        }

        public static string Unsecure(this SecureString secureString)
        {
            if (secureString is null)
            {
                throw new ArgumentNullException(nameof(secureString));
            }

            IntPtr unmanagedString = IntPtr.Zero;
            try
            {
                unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(secureString);
                return Marshal.PtrToStringUni(unmanagedString) ?? throw new InvalidOperationException($"{nameof(secureString)} is null");
            }
            finally
            {
                if (unmanagedString != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
                }
            }
        }

        public static SecureString Secure(this string notSecureString)
        {
            var secureString = new SecureString();
            foreach (char c in notSecureString)
            {
                secureString.AppendChar(c);
            }

            secureString.MakeReadOnly();
            return secureString;
        }
    }
}
