using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class DecodedRlp
    {
        public List<object> Items { get; }

        public object SingleItem { get; }

        public int Length => Items.Count;

        public bool IsSequence => Items != null;
        
        public DecodedRlp(object item)
        {
            SingleItem = item;
        }
        
        public DecodedRlp(List<object> items)
        {
            Items = items;
        }

        internal T As<T>()
        {
            if (Items == null)
            {
                return (T)SingleItem;
            }
            
            Debug.Assert(Items.Count == 0, $"Expected exactly one item in {nameof(DecodedRlp)}");
            return (T)Items[0];
        }

        public Keccak GetKeccak(int index)
        {
            return new Keccak((byte[])Items[index]);
        }
        
        public Address GetAddress(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? null : new Address(bytes);
        }

        public BigInteger GetUnsignedBigInteger(int index)
        {
            return ((byte[])Items[index]).ToUnsignedBigInteger();
        }

        public BigInteger GetSignedBigInteger(int index, int byteLength)
        {
            return ((byte[])Items[index]).ToSignedBigInteger(byteLength);
        }

        public byte GetByte(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? (byte)0 : bytes[0];
        }
        
        public object GetObject(int index)
        {
            return Items[index];
        }
        
        public int GetInt(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? 0 : bytes.ToInt32();
        }

        public byte[] GetBytes(int index)
        {
            return (byte[])Items[index];
        }

        public string GetString(int index)
        {
            return System.Text.Encoding.UTF8.GetString((byte[])Items[index]);
        }
        
        public T GetEnum<T>(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? (T)(object)0 : (T)(object)bytes[0];
        }
        
        public DecodedRlp GetSequence(int index)
        {
            return (DecodedRlp)Items[index];
        }
    }
}