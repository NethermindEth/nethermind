
namespace Nethermind.Db
{
    public interface ILogDecorder<T>
    {
        byte[] Decode(T encoded_value);
    }
}
