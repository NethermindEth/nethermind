using System.Collections.Generic;

namespace Nevermind.Core
{
    public class StateDatabase
    {
        public StateDatabase()
        {
            
        }

        public Dictionary<byte[], byte[]> State { get; } = new Dictionary<byte[], byte[]>();
    }

    public class World
    {
        public Dictionary<Address, Account> State { get; set; }
    }
}