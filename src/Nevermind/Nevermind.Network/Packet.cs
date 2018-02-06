namespace Nevermind.Network
{
    public class Packet
    {
        public Packet(byte[] data)
        {
            Data = data;
        }

        public byte[] Data;
    }
}