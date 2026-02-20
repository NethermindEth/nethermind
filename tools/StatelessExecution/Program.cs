// See https://aka.ms/new-console-template for more information

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Program
{
    static unsafe int Main()
    {
        var i = Convert.FromHexString("192c207ae0491ac1b74673d0f05126dc5a3c4fa0e6d277492fe6f3f6ebb4880c168b043bbbd7ae8e60606a7adf85c3602d0cd195af875ad061b5a6b1ef19b64507caa9e61fc843cf2f3769884e7467dd341a07fac1374f901d6e0da3f47fd2ec2b31ee53ccd0449de5b996cb8159066ba398078ec282102f016265ddec59c3541b38870e413a29c6b0b709e0705b55ab61ccc2ce24bbee322f97bb40b1732a4b28d255308f12e81dc16363f0f4f1410e1e9dd297ccc79032c0379aeb707822f9");
        Span<byte> input = stackalloc byte[192];
        i.CopyTo(input);

        fixed (byte* pair = input)
        {
            var s = Zisklib.bn254_pairing_check_c((nint)pair, 1);
            System.ZisK.WriteLine(s.ToString());
        }

        return 0;
    }

    public static partial class Zisklib
    {
        // [DllImport("__Internal")]
        // public static extern bool mul_bn254_c(nint p, nint k, nint o);

        [DllImport("__Internal")]
        public static extern byte bn254_pairing_check_c(nint pairs, nuint num_points);

        //[DllImport("__Internal")]
        //public static extern byte bn254_pairing_check_c_identity_pair_returns_success();
    }
}
