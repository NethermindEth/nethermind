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
using System.Runtime.InteropServices;
using System.Security;

namespace Nethermind.Crypto
{
    public static class SecureStringExtensions
    {
        public static byte[] ToByteArray(this SecureString secureString, System.Text.Encoding encoding = null)
        {
            if (secureString == null)
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
            if (secureString == null)
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
