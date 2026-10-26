using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFusionCAD;

/// <summary>Rust 엔진(libofc_core.so) C ABI 바인딩</summary>
internal static unsafe class NativeCore
{
    const string Lib = "ofc_core";

    /// 이벤트 JSON 을 받아 응답을 buf 에 쓰고 길이를 반환
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint EventCallback(IntPtr eventJson, IntPtr buf, nuint cap, IntPtr user);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int ofc_run(byte* requestJson, EventCallback cb, IntPtr user);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ofc_cancel();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr ofc_version();

    // GC 수거 방지를 위해 실행 중인 콜백을 붙잡아 둔다
    static EventCallback? _keepAlive;

    public static string Version => Marshal.PtrToStringUTF8(ofc_version()) ?? "?";

    /// <summary>블로킹 실행. 작업 스레드에서 호출할 것.</summary>
    public static int Run(string requestJson, Func<string, string?> onEvent)
    {
        _keepAlive = (evPtr, buf, cap, _) =>
        {
            var ev = Marshal.PtrToStringUTF8(evPtr) ?? "{}";
            string? resp;
            try { resp = onEvent(ev); }
            catch (Exception e) { Console.Error.WriteLine(e); resp = null; }
            if (string.IsNullOrEmpty(resp)) return 0;
            var bytes = Encoding.UTF8.GetBytes(resp);
            var n = Math.Min(bytes.Length, (int)cap);
            Marshal.Copy(bytes, 0, buf, n);
            return (nuint)n;
        };
        var req = Encoding.UTF8.GetBytes(requestJson + "\0");
        fixed (byte* p = req)
        {
            return ofc_run(p, _keepAlive, IntPtr.Zero);
        }
    }
}
