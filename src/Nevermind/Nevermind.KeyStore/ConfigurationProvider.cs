using System.Text;

namespace Nevermind.KeyStore
{
    public class ConfigurationProvider : IConfigurationProvider
    {
        public string KeyStoreDirectory => "KeyStore";
        public Encoding KeyStoreEncoding => Encoding.UTF8;
        public string Kdf => "scrypt";
        public string Cipher => "aes-128-cbc";
        public int KdfparamsDklen => 32;
        public int KdfparamsN => 262144;
        public int KdfparamsP => 1;
        public int KdfparamsR => 8;
        public int KdfparamsSaltLen => 32;
        public int SymmetricEncrypterBlockSize => 16;
        public int SymmetricEncrypterKeySize => 128;
    }
}