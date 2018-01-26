namespace Nevermind.Network
{
    public interface IMessagePad
    {
        byte[] Pad(byte[] bytes);
    }
}