using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

namespace Evm.JsonTypes;

public class T8NOutput
{
    public Dictionary<Address, NativePrestateTracerAccount>? Alloc { get; set; }
    public PostState? Result { get; set; }
    public byte[]? Body { get; set; }
    public string? ErrorMessage { get; set; }
    [JsonIgnore]
    public int ExitCode { get; set; }

    public T8NOutput() {}

    public T8NOutput(string errorMessage, int exitCode)
    {
        ErrorMessage = errorMessage;
        ExitCode = exitCode;
    }
}