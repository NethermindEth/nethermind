using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Personal
{
    public class AccountForRpc
    {
        public Address Address { get; set; }
        public bool Unlocked { get; set; }
    }
}