using System;
using System.Text;

namespace DfoServer.Infrastructure
{
    /// A21 客户端协议字符串用 GBK(936)。
    /// 日志、配置、哈希仍用 UTF-8。
    /// schema v11 把 v11 以下旧库的旧 UTF-8 线上名字节改成 GBK。
    public static class ClientTextEncoding
    {
        public const int CodePage = 936;

        private static readonly object Sync = new object();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        private static Encoding _gbk;

        public static Encoding Encoding
        {
            get
            {
                EnsureInitialized();
                return _gbk;
            }
        }

        public static void EnsureInitialized()
        {
            if (_gbk != null)
                return;

            lock (Sync)
            {
                if (_gbk != null)
                    return;

                System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _gbk = System.Text.Encoding.GetEncoding(CodePage);
            }
        }

        public static byte[] GetBytes(string value)
        {
            EnsureInitialized();
            return _gbk.GetBytes(value ?? string.Empty);
        }

        public static string GetString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;
            return GetString(bytes, 0, bytes.Length);
        }

        public static string GetString(byte[] bytes, int index, int count)
        {
            EnsureInitialized();
            if (bytes == null || count <= 0 || index < 0 || index + count > bytes.Length)
                return string.Empty;
            return _gbk.GetString(bytes, index, count).TrimEnd('\0');
        }

        public static bool TryGetStringStrict(byte[] bytes, out string text)
        {
            EnsureInitialized();
            text = string.Empty;
            if (bytes == null)
                return false;
            if (bytes.Length == 0)
                return true;

            try
            {
                text = System.Text.Encoding.GetEncoding(
                    CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// 把旧 UTF-8 线上字节改成 GBK。ASCII 和已是 GBK 的中文不变。
        internal static bool TryConvertLegacyUtf8WireToGbk(byte[] stored, out byte[] gbk)
        {
            EnsureInitialized();
            gbk = stored;
            if (stored == null || stored.Length == 0 || IsAscii(stored))
                return false;

            string text;
            try
            {
                text = StrictUtf8.GetString(stored);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            if (text.IndexOf('\uFFFD') >= 0)
                return false;

            var converted = GetBytes(text);
            if (BytesEqual(converted, stored))
                return false;

            gbk = converted;
            return true;
        }

        public static byte[] Truncate(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0)
                return Array.Empty<byte>();

            var bytes = GetBytes(value);
            if (bytes.Length <= maxBytes)
                return bytes;

            var used = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                var n = Encoding.GetByteCount(rune.ToString());
                if (used + n > maxBytes)
                    break;
                used += n;
            }

            if (used <= 0)
                return Array.Empty<byte>();

            var truncated = new byte[used];
            Buffer.BlockCopy(bytes, 0, truncated, 0, used);
            return truncated;
        }

        public static int ClampPrefixLength(byte[] bytes, int maxBytes)
        {
            if (bytes == null || bytes.Length == 0 || maxBytes <= 0)
                return 0;
            if (bytes.Length <= maxBytes)
                return bytes.Length;

            var i = 0;
            var lastSafe = 0;
            while (i < maxBytes)
            {
                if (bytes[i] >= 0x81)
                {
                    if (i + 1 >= maxBytes)
                        break;
                    i += 2;
                    lastSafe = i;
                }
                else
                {
                    i++;
                    lastSafe = i;
                }
            }

            return lastSafe;
        }

        private static bool IsAscii(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] > 0x7F)
                    return false;
            }

            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
