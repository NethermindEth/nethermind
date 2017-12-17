using System;
using Nevermind.Core;
using Newtonsoft.Json;

namespace Nevermind.Json
{
    public class JsonSerializer : IJsonSerializer
    {
        private readonly ILogger _logger;

        public JsonSerializer(ILogger logger)
        {
            _logger = logger;
        }

        public T DeserializeObject<T>(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception e)
            {
                _logger.Error("Error during json deserialization", e);
                return default(T);
            }           
        }

        public string SerializeObject<T>(T value)
        {
            try
            {
                return JsonConvert.SerializeObject(value, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
            }
            catch (Exception e)
            {
                _logger.Error("Error during json serialization", e);
                return null;
            }          
        }
    }
}