// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Stats.Model
{
    public class Capability : IEquatable<Capability>
    {
        public Capability(string protocolCode, int version)
        {
            ProtocolCode = protocolCode;
            Version = version;
        }

        public string ProtocolCode { get; }
        public int Version { get; }

        public bool Equals(Capability other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return string.Equals(ProtocolCode, other.ProtocolCode) && Version == other.Version;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Capability)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ProtocolCode, Version);
        }

        public override string ToString()
        {
            return string.Concat(ProtocolCode, Version);
        }
    }
}
