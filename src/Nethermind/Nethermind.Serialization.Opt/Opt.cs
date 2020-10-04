using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Serialization.Opt
{
    public static class Opt
    {
        public static byte[] Encode(Account account)
        {
            if (account == null)
            {
                return null;
            }
            
            try
            {
                int nonceLength = Rlp.Rlp.LengthOf(account.Nonce);

                int totalLength =
                    nonceLength +
                    Rlp.Rlp.LengthOf(account.Balance) +
                    (account.HasCode ? 33 : 0);

                byte[] bytes = new byte[totalLength];
                RlpStream rlpStream = new RlpStream(bytes);
                rlpStream.Encode(account.Nonce);
                rlpStream.Encode(account.Balance);
                if (account.HasCode)
                {
                    rlpStream.Encode(account.CodeHash);
                }

                return rlpStream.Data;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public static Account DecodeAccount(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            try
            {
                RlpStream rlpStream = new RlpStream(bytes);
                UInt256 nonce = rlpStream.DecodeUInt256();
                UInt256 balance = rlpStream.DecodeUInt256();
                Keccak codeHash = Keccak.OfAnEmptyString;
                if (rlpStream.ReadNumberOfItemsRemaining() > 0)
                {
                    codeHash = rlpStream.DecodeKeccak();
                }

                Account account = new Account(nonce, balance, Keccak.EmptyTreeHash, codeHash);
                return account;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
    }
}