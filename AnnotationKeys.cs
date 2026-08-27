using System;
using System.Security.Cryptography;
using System.Text;

namespace MicaPDF
{
    public static class AnnotationKeys
    {
        public static string KeyFor(string path, long fileLength)
        {
            var raw = $"{path.ToLowerInvariant()}|{fileLength}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }
    }
}
