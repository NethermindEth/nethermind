namespace Nevermind.Json
{
    public interface IJsonSerializer
    {
        T DeserializeAnonymousType<T>(string json, T definition);
        T DeserializeObject<T>(string json);
        string SerializeObject<T>(T value);
    }
}