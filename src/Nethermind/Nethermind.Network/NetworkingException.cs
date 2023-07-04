// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network
{
    public class NetworkingException : Exception
    {
        public NetworkingException(string message, NetworkExceptionType networkExceptionType)
            : base(message)
        {
            NetworkExceptionType = networkExceptionType;
        }

        public NetworkingException(string message, NetworkExceptionType networkExceptionType, Exception innerException) : base(message, innerException)
        {
            NetworkExceptionType = networkExceptionType;
        }

        public NetworkExceptionType NetworkExceptionType { get; set; }
    }
}
