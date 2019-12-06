using System;
using System.Linq;
using Newtonsoft.Json;

namespace Cortex.BeaconNode.Api
{
    //internal class ByteToPrefixedHexNewtonsoftJsonConverter : JsonConverter
    //{
    //    private const string Prefix = "0x";

    //    public override bool CanConvert(Type objectType)
    //    {
    //        return objectType == typeof(byte[]);
    //    }

    //    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, Newtonsoft.Json.JsonSerializer serializer)
    //    {
    //        if (objectType == typeof(string))
    //        {
    //            var hex = serializer.Deserialize<string>(reader);
    //            if (!string.IsNullOrEmpty(hex) && hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
    //            {
    //                return Enumerable.Range(Prefix.Length, hex.Length - Prefix.Length)
    //                     .Where(x => x % 2 == 0)
    //                     .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
    //                     .ToArray();
    //            }
    //        }
    //        return new byte[0];
    //    }

    //    public override void WriteJson(JsonWriter writer, object value, Newtonsoft.Json.JsonSerializer serializer)
    //    {
    //        var bytes = value as byte[];
    //        var stringValue = Prefix + BitConverter.ToString(bytes).Replace("-", string.Empty);
    //        serializer.Serialize(writer, stringValue);
    //    }
    //}
}
