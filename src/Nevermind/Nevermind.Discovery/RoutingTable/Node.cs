using System;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Discovery.RoutingTable
{
    public class Node
    {
        public Node(byte[] id)
        {
            Id = id;
            IdHash = Keccak.Compute(id).Bytes;
            IdHashText = IdHash.ToString();
        }

        public byte[] Id { get; }
        public byte[] IdHash { get; }
        public string IdHashText { get; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool IsDicoveryNode { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is Node item)
            {
                return Bytes.UnsafeCompare(IdHash, item.IdHash);
            }
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return IdHash.GetHashCode();
        }
    }
}