using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    internal class KeccakOrRlp
    {
        public bool IsKeccak { get; }

        public KeccakOrRlp(Keccak keccak)
        {
            _keccak = keccak;
            IsKeccak = true;
        }

        public KeccakOrRlp(Rlp rlp)
        {
            if (rlp.Bytes.Length < 32)
            {
                _rlp = rlp;
            }
            else
            {
                _keccak = Keccak.Compute(rlp);
                IsKeccak = true;
            }
        }

        private Rlp _rlp;
        private Keccak _keccak;

        public byte[] Bytes => _rlp?.Bytes ?? _keccak.Bytes;

        public Keccak GetOrComputeKeccak()
        {
            return _keccak ?? (_keccak = Keccak.Compute(_rlp));
        }

        public Rlp GetOrEncodeRlp()
        {
            return _rlp ?? (_rlp = Rlp.Serialize(_keccak.Bytes));
        }

        public override string ToString()
        {
            if (IsKeccak)
            {
                string full = Hex.FromBytes(Bytes, IsKeccak);
                return IsKeccak ? full.Substring(0, 6) : full;
            }

            return PatriciaTree.RlpDecode(new Rlp(Bytes)).ToString();
        }
    }
}