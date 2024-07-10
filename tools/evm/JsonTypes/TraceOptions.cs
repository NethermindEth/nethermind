namespace Evm.JsonTypes;

public class TraceOptions
{
    public bool IsEnabled { get; set; }
    public bool Memory { get; set; }
    public bool NoStack { get; set; }
    public bool ReturnData { get; set; }
}