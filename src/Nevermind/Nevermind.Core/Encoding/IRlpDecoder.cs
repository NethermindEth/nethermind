namespace Nevermind.Core.Encoding
{
    public interface IRlpDecoder
    {
    }

    public interface IRlpDecoder<out T> : IRlpDecoder
    {
        T Decode(Rlp rlp);
    }
}