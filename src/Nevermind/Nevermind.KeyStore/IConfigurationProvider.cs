using System.Text;

namespace Nevermind.KeyStore
{
    public interface IConfigurationProvider
    {
        string KeyStoreDirectory { get; }
        Encoding KeyStoreEncoding { get; }

        string Kdf { get; }
        string Cipher { get; }
        int KdfparamsDklen { get; }
        int KdfparamsN { get; }
        int KdfparamsP { get; }
        int KdfparamsR { get; }
        int KdfparamsSaltLen { get; }

        int SymmetricEncrypterBlockSize { get; }
        int SymmetricEncrypterKeySize { get; }
        int IVSize { get; }
    }
}
