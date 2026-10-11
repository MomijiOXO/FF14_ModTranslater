using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModTranslater.Core;

public sealed class CredentialVault(string path)
{
    private readonly object sync = new();
    public bool Has(TranslationProvider provider)
    {
        lock (sync) return Load().ContainsKey(provider.ToString());
    }
    private Dictionary<string, string> Load() => JsonStorage.Load(path, () => new Dictionary<string, string>());
    public void Save(TranslationProvider provider, ApiCredentials credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = WindowsProtection.Protect(plain);
            lock (sync) { var entries = Load(); entries[provider.ToString()] = Convert.ToBase64String(encrypted); JsonStorage.Save(path, entries); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete(TranslationProvider provider)
    {
        lock (sync) { var entries = Load(); entries.Remove(provider.ToString()); JsonStorage.Save(path, entries); }
    }
    public ApiCredentials Read(TranslationProvider provider)
    {
        string encrypted;
        lock (sync) encrypted = Load().GetValueOrDefault(provider.ToString()) ?? throw new InvalidOperationException("请先保存此服务的凭证。");
        byte[]? plain = null;
        try
        {
            plain = WindowsProtection.Unprotect(Convert.FromBase64String(encrypted));
            return JsonSerializer.Deserialize<ApiCredentials>(plain) ?? throw new CryptographicException("凭证格式无效。");
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        { throw new CryptographicException("无法解密凭证，请在当前 Windows 账户下重新填写。"); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
}
public static class WindowsProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭证加密需要 Windows。");
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            // CRYPTPROTECT_UI_FORBIDDEN; deliberately omit CRYPTPROTECT_LOCAL_MACHINE.
            var ok = encrypt ? CryptProtectData(ref input, "ModTranslater", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException($"DPAPI Windows error {Marshal.GetLastWin32Error()}");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
        }
    }
}
