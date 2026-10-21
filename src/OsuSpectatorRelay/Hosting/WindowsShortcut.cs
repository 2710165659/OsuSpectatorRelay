using System.Runtime.InteropServices;

namespace OsuSpectatorRelay.Hosting;

// Use the stable Windows shell COM ABI directly. This avoids reflection-based COM
// wrappers, which are not compatible with a fully trimmed .NET runtime.
internal static class WindowsShortcut
{
    public static void Save(string path, string executable, string arguments, string workingDirectory, string iconPath)
    {
        int initialized = CoInitializeEx(0, 2); // apartment-threaded; an existing apartment is also fine
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        nint link = 0, persist = 0;
        try
        {
            var classId = new Guid("00021401-0000-0000-C000-000000000046");
            var linkId = new Guid("000214F9-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref linkId, out link));
            // IShellLinkW vtable: SetDescription=7, SetWorkingDirectory=9,
            // SetArguments=11, SetIconLocation=17, SetPath=20 (including IUnknown).
            SetString(link, 20, executable);
            SetString(link, 11, arguments);
            SetString(link, 9, workingDirectory);
            SetString(link, 7, "自动启动旁观代理，并跟随 Windows 系统代理切换线路");
            Marshal.ThrowExceptionForHR(Method<SetIcon>(link, 17)(link, iconPath, 0));
            var persistId = new Guid("0000010B-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<QueryInterface>(link, 0)(link, ref persistId, out persist));
            Marshal.ThrowExceptionForHR(Method<SaveFile>(persist, 6)(persist, path, true));
        }
        finally
        {
            if (persist != 0) Method<Release>(persist, 2)(persist);
            if (link != 0) Method<Release>(link, 2)(link);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void SetString(nint instance, int slot, string value)
        => Marshal.ThrowExceptionForHR(Method<SetText>(instance, slot)(instance, value));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(nint instance, ref Guid interfaceId, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetText(nint instance, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetIcon(nint instance, [MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SaveFile(nint instance, [MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool remember);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint instance);
}
