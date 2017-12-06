using Newtonsoft.Json;

namespace Nevermind.JsonRpc
{
    public class JsonSerializer : IJsonSerializer
    {
        public T DeserializeObject<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public string SerializeObject<T>(T value)
        {
            return JsonConvert.SerializeObject(value, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }
    }
}