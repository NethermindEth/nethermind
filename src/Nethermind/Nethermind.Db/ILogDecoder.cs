using System;
namespace Nethermind.Db
{
    public interface ILogDecoder<I, O>
    {
        void Decode(I[] encoded_data, Span<O> output);
    }
}
