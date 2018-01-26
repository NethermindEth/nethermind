namespace Nevermind.Network
{
    public class NoPad : IMessagePad
    {
        public byte[] Pad(byte[] bytes)
        {
            return bytes;
        }
    }
}