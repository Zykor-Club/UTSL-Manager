using System;
using System.Runtime.InteropServices;

namespace UTSLManager.Servers;

/// <summary>
/// Windows DPAPI（当前用户作用域）加解密：令牌只以密文落盘。
/// 从 MainWindow 里搬出来的同一份实现（行为不变），供 servers.json 读写复用。
/// </summary>
internal static class SecretBox
{
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob pDataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob pDataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    /// <summary>加密（只有本机本用户能解开）。失败返回空串 —— 调用方会退回明文并如实提示。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) { return ""; }
        byte[] inBytes = System.Text.Encoding.UTF8.GetBytes(plain);
        IntPtr buf = Marshal.AllocHGlobal(inBytes.Length);
        try
        {
            Marshal.Copy(inBytes, 0, buf, inBytes.Length);
            var inBlob = new DataBlob { cbData = inBytes.Length, pbData = buf };
            if (!CryptProtectData(ref inBlob, "UTSLManager", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outBlob)) { return ""; }
            try
            {
                byte[] outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Convert.ToBase64String(outBytes);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string Unprotect(string b64)
    {
        if (string.IsNullOrEmpty(b64)) { return ""; }
        try
        {
            byte[] inBytes = Convert.FromBase64String(b64);
            IntPtr buf = Marshal.AllocHGlobal(inBytes.Length);
            try
            {
                Marshal.Copy(inBytes, 0, buf, inBytes.Length);
                var inBlob = new DataBlob { cbData = inBytes.Length, pbData = buf };
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outBlob)) { return ""; }
                try
                {
                    byte[] outBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                    return System.Text.Encoding.UTF8.GetString(outBytes);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
    }
}
