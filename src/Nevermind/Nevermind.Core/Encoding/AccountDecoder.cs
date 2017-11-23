using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Encoding
{
    public class AccountDecoder : IRlpDecoder<Account>
    {
        public Account Decode(Rlp rlp)
        {
            Account account = new Account();
            object[] data = (object[]) Rlp.Decode(rlp);
            account.Nonce = ((byte[])data[0]).ToUnsignedBigInteger();
            account.Balance = ((byte[]) data[1]).ToUnsignedBigInteger();
            account.StorageRoot = new Keccak((byte[])data[2]);
            account.CodeHash = new Keccak((byte[]) data[3]);
            return account;
        }
    }
}