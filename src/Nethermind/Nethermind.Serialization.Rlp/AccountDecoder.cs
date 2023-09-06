// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class AccountDecoder : IRlpObjectDecoder<Account?>, IRlpStreamDecoder<Account?>
    {
        private readonly bool _slimFormat;

        public AccountDecoder() { }

        public AccountDecoder(bool slimFormat = false)
        {
            _slimFormat = slimFormat;
        }

        public (Keccak CodeHash, Keccak StorageRoot) DecodeHashesOnly(RlpStream rlpStream)
        {
            rlpStream.SkipLength();
            rlpStream.SkipItem();
            rlpStream.SkipItem();

            Keccak storageRoot = DecodeStorageRoot(rlpStream);
            Keccak codeHash = DecodeCodeHash(rlpStream);

            return (codeHash, storageRoot);
        }

        public Keccak DecodeStorageRootOnly(RlpStream rlpStream)
        {
            rlpStream.SkipLength();
            rlpStream.SkipItem();
            rlpStream.SkipItem();
            Keccak storageRoot = DecodeStorageRoot(rlpStream);
            return storageRoot;
        }

        public Account? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = rlpStream.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }

            UInt256 nonce = rlpStream.DecodeUInt256();
            UInt256 balance = rlpStream.DecodeUInt256();
            Keccak storageRoot = DecodeStorageRoot(rlpStream);
            Keccak codeHash = DecodeCodeHash(rlpStream);
            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        public void Encode(RlpStream stream, Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            Encode(item, stream);
        }

        public Rlp Encode(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            int contentLength = GetContentLength(item);
            RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

            Encode(item, rlpStream, contentLength);

            return new Rlp(rlpStream.Data);
        }

        public void Encode(Account account, RlpStream rlpStream, int? contentLength = null)
        {
            contentLength ??= GetContentLength(account);

            rlpStream.StartSequence(contentLength.Value);
            rlpStream.Encode(account.Nonce);
            rlpStream.Encode(account.Balance);

            if (_slimFormat && !account.HasStorage)
            {
                rlpStream.EncodeEmptyByteArray();
            }
            else
            {
                rlpStream.Encode(account.StorageRoot);
            }

            if (_slimFormat && !account.HasCode)
            {
                rlpStream.EncodeEmptyByteArray();
            }
            else
            {
                rlpStream.Encode(account.CodeHash);
            }
        }

        public int GetLength(Account[] accounts)
        {
            int length = 0;

            if (accounts is null || accounts.Length == 0)
            {
                return 1;
            }

            for (int i = 0; i < accounts.Length; i++)
            {
                length += GetLength(accounts[i]);
            }

            return length;
        }

        public int GetLength(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item));
        }

        public int GetContentLength(Account? item)
        {
            if (item is null)
            {
                return 0;
            }

            var contentLength = Rlp.LengthOf(item.Nonce);
            contentLength += Rlp.LengthOf(item.Balance);

            if (_slimFormat && !item.HasStorage)
            {
                contentLength++;
            }
            else
            {
                contentLength += Rlp.LengthOfKeccakRlp;
            }

            if (_slimFormat && !item.HasCode)
            {
                contentLength++;
            }
            else
            {
                contentLength += Rlp.LengthOfKeccakRlp;
            }

            return contentLength;
        }

        private Keccak DecodeStorageRoot(RlpStream rlpStream)
        {
            Keccak storageRoot;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                storageRoot = Keccak.EmptyTreeHash;
            }
            else
            {
                storageRoot = rlpStream.DecodeKeccak();
            }

            return storageRoot;
        }

        private Keccak DecodeCodeHash(RlpStream rlpStream)
        {
            Keccak codeHash;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                codeHash = Keccak.OfAnEmptyString;
            }
            else
            {
                codeHash = rlpStream.DecodeKeccak();
            }

            return codeHash;
        }
    }
}
