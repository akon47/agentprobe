using System.Runtime.InteropServices;
using AgentProbe.Native;

namespace AgentProbe.Util;

/// <summary>Windows DPAPI (current user scope) for secrets written to disk.</summary>
internal static unsafe class Dpapi
{
    public static byte[] Protect(ReadOnlySpan<byte> data) => Run(data, protect: true);

    public static byte[] Unprotect(ReadOnlySpan<byte> data) => Run(data, protect: false);

    private static byte[] Run(ReadOnlySpan<byte> data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new DATA_BLOB { cbData = (uint)data.Length, pbData = (nint)p };
            var output = default(DATA_BLOB);
            var ok = protect
                ? Win32.CryptProtectData(&input, AppInfo.Name, null, 0, 0, 1, &output) // CRYPTPROTECT_UI_FORBIDDEN
                : Win32.CryptUnprotectData(&input, 0, null, 0, 0, 1, &output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                return new ReadOnlySpan<byte>((void*)output.pbData, (int)output.cbData).ToArray();
            }
            finally
            {
                Win32.LocalFree(output.pbData);
            }
        }
    }
}
