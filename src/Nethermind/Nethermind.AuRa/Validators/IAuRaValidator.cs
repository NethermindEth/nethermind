using Nethermind.Core;

namespace Nethermind.AuRa.Validators
{
    public interface IAuRaValidator
    {
        bool IsValidSealer(Address address);
    }
}