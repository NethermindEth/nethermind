namespace Nethermind.EvmPlayground
{
    internal interface IJsonSerializer
    {
        T Deserialize<T>(string json);
        string Serialize<T>(T value, bool indented = false);
    }
}