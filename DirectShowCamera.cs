using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;

namespace AGLPTZ;

internal record CameraInfo(string Name, string MonikerDisplayName)
{
    public bool IsVirtual => VirtualName(Name);
    internal static bool VirtualName(string name) => Regex.IsMatch(name,
        @"virtual|виртуальн|\b(?:obs|ndi|camo|manycam|droidcam|xsplit|epoccam|iriun)\b|snap camera|nvidia broadcast",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public override string ToString() => Name;
}

internal record CameraRange(int Min, int Max, int Step, int Default, int Caps)
{
    public bool Manual => (Caps & 2) != 0;
}

// DirectShow's CameraControlProperty enum.
internal enum CameraProp { Pan = 0, Tilt = 1, Roll = 2, Zoom = 3, Exposure = 4, Iris = 5, Focus = 6 }

[ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    [PreserveSig] int CreateClassEnumerator(ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
}

[ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
        [MarshalAs(UnmanagedType.Struct)] ref object pVar, IntPtr pErrorLog);
    [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
        [MarshalAs(UnmanagedType.Struct)] ref object pVar);
}

[ComImport, Guid("C6E13370-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMCameraControl
{
    [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int flags);
    [PreserveSig] int Set(int property, int value, int flags);
    [PreserveSig] int Get(int property, out int value, out int flags);
}

internal sealed class DirectShowCamera : IDisposable
{
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private readonly object filter;
    private readonly IAMCameraControl? controller;
    private const string TestMoniker = "agl-test-usb";
    // Opt-in process-local test fixture. It never opens a real camera.
    internal static bool TestMode => Environment.GetEnvironmentVariable("AGL_PTZ_TEST") == "1";
    private bool IsTest => filter is Dictionary<CameraProp, int>;
    public bool HasInterface => controller != null || IsTest;

    private DirectShowCamera(object filter)
    {
        this.filter = filter;
        controller = filter as IAMCameraControl;
    }

    public static List<CameraInfo> List()
    {
        if (TestMode) return [new("Тестовая USB-камера", TestMoniker)];
        var list = new List<CameraInfo>();
        object deviceEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!)!;
        IEnumMoniker? enumerator = null;
        try
        {
            Guid category = VideoInputCategory;
            int hr = ((ICreateDevEnum)deviceEnumerator).CreateClassEnumerator(ref category, out enumerator, 0);
            if (hr != 0 || enumerator == null) return list;
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(null, null, out string displayName);
                    string friendlyName = displayName;
                    object? bag = null;
                    try
                    {
                        Guid iidBag = typeof(IPropertyBag).GUID;
                        moniker.BindToStorage(null, null, ref iidBag, out bag);
                        object value = "";
                        if (((IPropertyBag)bag).Read("FriendlyName", ref value, IntPtr.Zero) == 0)
                            friendlyName = value?.ToString() ?? displayName;
                    }
                    catch (COMException) { }
                    finally { Release(bag); }
                    list.Add(new CameraInfo(friendlyName, displayName));
                }
                finally { Release(moniker); }
            }
        }
        finally { Release(enumerator); Release(deviceEnumerator); }
        return list;
    }

    public static DirectShowCamera Open(CameraInfo selected)
    {
        if (TestMode && selected.MonikerDisplayName == TestMoniker) return new(new Dictionary<CameraProp, int>());
        object filter = Marshal.BindToMoniker(selected.MonikerDisplayName);
        return new DirectShowCamera(filter);
    }

    public CameraRange? Range(CameraProp p)
    {
        if (IsTest) return p == CameraProp.Zoom ? new(0, 10000, 1, 0, 2) : new(-10000, 10000, 1, 0, 2);
        if (controller == null) return null;
        int hr = controller.GetRange((int)p, out int min, out int max, out int step, out int def, out int flags);
        return hr == 0 ? new(min, max, Math.Max(1, step), def, flags) : null;
    }

    public int? Get(CameraProp p)
    {
        if (filter is Dictionary<CameraProp, int> test) return test.GetValueOrDefault(p);
        if (controller == null) return null;
        return controller.Get((int)p, out int val, out _) == 0 ? val : null;
    }

    public bool Set(CameraProp p, int value)
    {
        if (filter is Dictionary<CameraProp, int> test)
        {
            test[p] = value;
            File.AppendAllText(Path.Combine(AppPaths.DataRoot, "test-motion.log"),
                $"{Environment.TickCount64} {p} {value}{Environment.NewLine}");
            return true;
        }
        if (controller == null) return false;
        return controller.Set((int)p, value, 2 /* CameraControl_Flags_Manual */) == 0;
    }

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
    public void Dispose() => Release(filter);
}
