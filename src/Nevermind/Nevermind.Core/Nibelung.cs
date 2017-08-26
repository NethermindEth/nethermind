namespace Nevermind.Core
{
    public class Nibelung
    {
        public Nibelung(bool flag, params byte[] nibbles)
        {
            Flag = flag;
            Nibbles = nibbles;
        }

        public byte[] Nibbles { get; set; }
        public bool Flag { get; set; }
    }
}