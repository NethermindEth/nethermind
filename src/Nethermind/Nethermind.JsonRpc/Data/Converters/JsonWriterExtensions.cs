using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data.Converters
{
    public static class JsonWriterExtensions
    {
        public static void WriteProperty<T>(this JsonWriter jsonWriter, string propertyName, T propertyValue)
        {
            jsonWriter.WritePropertyName(propertyName);
            jsonWriter.WriteValue(propertyValue);
        }
    }
}