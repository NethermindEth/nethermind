namespace Nevermind.Core
{
    public class Address
    {
        private readonly byte[] _address;

        public Address(byte[] bytes)
        {
            _address = bytes;
        }

        public override string ToString()
        {
            return HexString.FromBytes(_address);
        }
    }
}
