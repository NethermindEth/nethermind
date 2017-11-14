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
            if (!IsKeccak)
            {
                _keccak = Keccak.Compute(_rlp);
            }

            return _keccak;
        }

        public Rlp GetOrEncodeRlp()
        {
            return _rlp ?? (_rlp = Rlp.Encode(_keccak.Bytes));
        }

        public override string ToString()
        {
            return IsKeccak
                ? _keccak.ToString(true).Substring(0, 6)
                : PatriciaTree.RlpDecode(new Rlp(Bytes)).ToString();
        }
    }
}