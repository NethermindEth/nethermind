using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Evm.JsonTypes;

public class T8NOutput
{
    public PostState? Result { get; set; }
    public Dictionary<Address, Account>? Alloc { get; set; }
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