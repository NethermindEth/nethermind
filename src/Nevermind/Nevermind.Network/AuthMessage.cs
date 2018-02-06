using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class AuthMessage : AuthMessageBase
    {
        public Keccak EphemeralPublicHash { get; set; }
        public bool IsTokenUsed { get; set; }
    }
}