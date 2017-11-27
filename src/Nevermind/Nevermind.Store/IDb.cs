using System;
using Nevermind.Core.Crypto;

namespace Nevermind.Store
{
    public interface IDb : ISnapshotable
    {
        byte[] this[Keccak key]
        {
            get;
            set;
        }

        void Delete(Keccak key);

        void Print(Action<string> printFunc);
    }
}