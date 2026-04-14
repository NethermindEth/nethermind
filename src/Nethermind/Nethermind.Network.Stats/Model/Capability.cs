// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;

namespace Nethermind.Stats.Model
{
    [JsonConverter(typeof(CapabilityConverter))]
    public class Capability(string protocolCode, int version) : IEquatable<Capability>
    {
        public string ProtocolCode { get; } = protocolCode;
        public int Version { get; } = version;

        public bool Equals(Capability other)
        {
            if (other is null)
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
            if (obj is null)
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Capability)obj);
        }

        public override int GetHashCode() => HashCode.Combine(ProtocolCode, Version);

        public override string ToString() => string.Concat(ProtocolCode, Version);
    }
}
