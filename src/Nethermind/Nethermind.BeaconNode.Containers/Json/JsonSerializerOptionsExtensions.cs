using System.Text.Json;

namespace Nethermind.BeaconNode.Containers.Json
{
    public static class JsonSerializerOptionsExtensions
    {
        public static void AddCortexContainerConverters(this JsonSerializerOptions options)
        {
            options.Converters.Add(new JsonConverterBlsPublicKey());
            options.Converters.Add(new JsonConverterBlsSignature());
            options.Converters.Add(new JsonConverterBytes32());
            options.Converters.Add(new JsonConverterCommitteeIndex());
            options.Converters.Add(new JsonConverterDomain());
            options.Converters.Add(new JsonConverterEpoch());
            options.Converters.Add(new JsonConverterForkVersion());
            options.Converters.Add(new JsonConverterGwei());
            options.Converters.Add(new JsonConverterHash32());
            options.Converters.Add(new JsonConverterShard());
            options.Converters.Add(new JsonConverterSlot());
            options.Converters.Add(new JsonConverterValidatorIndex());
        }
    }
}
