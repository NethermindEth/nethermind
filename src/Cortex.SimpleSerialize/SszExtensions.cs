namespace Cortex.SimpleSerialize
{
    public static class BasicVectorExtensions
    {
        public static SszElement ToSszBasicElement(this ulong item)
        {
            return new SszBasicElement(item);
        }

        public static SszElement ToSszBasicVector(this byte[] item)
        {
            return new SszBasicVector(item);
        }
    }
}
