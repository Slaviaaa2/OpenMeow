using System.Runtime.InteropServices;
using System.Text;

namespace OpenMeow.Driver;

// openvr_driver.h(ValveSoftware/openvr master、2026-07 取得)から転記した定数・構造体。
// 値はすべてヘッダ現物で確認済み。

internal static class VR
{
    // インターフェースバージョン文字列
    public const string IServerTrackedDeviceProvider_Version = "IServerTrackedDeviceProvider_004";
    public const string ITrackedDeviceServerDriver_Version = "ITrackedDeviceServerDriver_005";
    public const string IVRDisplayComponent_Version = "IVRDisplayComponent_003";
    public const string IVRVirtualDisplay_Version = "IVRVirtualDisplay_002";
    public const string IVRDriverInput_Version = "IVRDriverInput_004";
    public const string IVRServerDriverHost_Version = "IVRServerDriverHost_006";
    public const string IVRProperties_Version = "IVRProperties_001";
    public const string IVRDriverLog_Version = "IVRDriverLog_001";
    public const string IVRSettings_Version = "IVRSettings_003";

    // EVRInitError
    public const int VRInitError_None = 0;
    public const int VRInitError_Init_InterfaceNotFound = 105;
    public const int VRInitError_Driver_Failed = 200;

    // ETrackedDeviceClass
    public const int TrackedDeviceClass_HMD = 1;
    public const int TrackedDeviceClass_Controller = 2;

    // ETrackedControllerRole
    public const int TrackedControllerRole_LeftHand = 1;
    public const int TrackedControllerRole_RightHand = 2;

    // ETrackingResult
    public const int TrackingResult_Running_OK = 200;

    // PropertyTypeTag_t
    public const uint FloatPropertyTag = 1;
    public const uint Int32PropertyTag = 2;
    public const uint Uint64PropertyTag = 3;
    public const uint BoolPropertyTag = 4;
    public const uint StringPropertyTag = 5;

    // ETrackedDeviceProperty
    public const int Prop_TrackingSystemName_String = 1000;
    public const int Prop_ModelNumber_String = 1001;
    public const int Prop_SerialNumber_String = 1002;
    public const int Prop_RenderModelName_String = 1003;
    public const int Prop_ManufacturerName_String = 1005;
    public const int Prop_ContainsProximitySensor_Bool = 1025;
    public const int Prop_InputProfilePath_String = 1037;
    public const int Prop_SecondsFromVsyncToPhotons_Float = 2001;
    public const int Prop_DisplayFrequency_Float = 2002;
    public const int Prop_UserIpdMeters_Float = 2003;
    public const int Prop_CurrentUniverseId_Uint64 = 2004;
    public const int Prop_IsOnDesktop_Bool = 2007;
    public const int Prop_UserHeadToEyeDepthMeters_Float = 2026;
    public const int Prop_DisplayDebugMode_Bool = 2044;
    public const int Prop_ControllerRoleHint_Int32 = 3007;
    public const int Prop_ControllerType_String = 7000;

    // EPropertyWriteType
    public const int PropertyWrite_Set = 0;

    // EVRScalarType / EVRScalarUnits
    public const int VRScalarType_Absolute = 0;
    public const int VRScalarUnits_NormalizedOneSided = 0;
    public const int VRScalarUnits_NormalizedTwoSided = 1;

    // EVREye
    public const int Eye_Left = 0;
    public const int Eye_Right = 1;

    public const uint InvalidTrackedDeviceIndex = 0xFFFFFFFF;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HmdQuaternion
{
    public double W, X, Y, Z; // ヘッダ通り w が先頭

    public static readonly HmdQuaternion Identity = new() { W = 1 };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DriverPose
{
    public double PoseTimeOffset;
    public HmdQuaternion QWorldFromDriverRotation;
    public fixed double VecWorldFromDriverTranslation[3];
    public HmdQuaternion QDriverFromHeadRotation;
    public fixed double VecDriverFromHeadTranslation[3];
    public fixed double VecPosition[3];
    public fixed double VecVelocity[3];
    public fixed double VecAcceleration[3];
    public HmdQuaternion QRotation;
    public fixed double VecAngularVelocity[3];
    public fixed double VecAngularAcceleration[3];
    public int Result;
    public byte PoseIsValid;
    public byte WillDriftInYaw;
    public byte ShouldApplyHeadModel;
    public byte DeviceIsConnected;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DistortionCoordinates
{
    public float RedU, RedV, GreenU, GreenV, BlueU, BlueV;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HmdMatrix34
{
    public fixed float M[12]; // 3行4列、[row*4+col]

    public static HmdMatrix34 Translation(float x, float y, float z)
    {
        var m = new HmdMatrix34();
        m.M[0] = 1; m.M[5] = 1; m.M[10] = 1;
        m.M[3] = x; m.M[7] = y; m.M[11] = z;
        return m;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyWrite
{
    public int Prop;
    public int WriteType;
    public int SetError;
    public IntPtr Buffer;
    public uint BufferSize;
    public uint Tag;
    public int Error;
}

/// <summary>vrserver 側 C++ オブジェクトの vtable スロットを直接呼ぶためのヘルパー。</summary>
internal static unsafe class Vtbl
{
    public static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    /// <summary>NUL 終端 ANSI 文字列(プロセス生存中は解放しない)。</summary>
    public static byte* StaticAnsi(string s)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(s);
        byte* p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        for (int i = 0; i < bytes.Length; i++) p[i] = bytes[i];
        p[bytes.Length] = 0;
        return p;
    }

    public static string ReadAnsi(byte* p)
    {
        if (p == null) return "";
        int len = 0;
        while (p[len] != 0) len++;
        return Encoding.ASCII.GetString(p, len);
    }
}

/// <summary>IVRDriverContext(vrserver 提供)。</summary>
internal static unsafe class DriverContext
{
    // vtable: 0=GetGenericInterface, 1=GetDriverHandle
    public static IntPtr GetGenericInterface(IntPtr ctx, string version, out int error)
    {
        var fn = (delegate* unmanaged<IntPtr, byte*, int*, IntPtr>)Vtbl.Slot(ctx, 0);
        byte* name = Vtbl.StaticAnsi(version);
        int err = 0;
        IntPtr result = fn(ctx, name, &err);
        error = err;
        return result;
    }
}

/// <summary>IVRServerDriverHost_006(vrserver 提供)。</summary>
internal static unsafe class DriverHost
{
    // vtable: 0=TrackedDeviceAdded, 1=TrackedDevicePoseUpdated, 2=VsyncEvent,
    // 3=VendorSpecificEvent, 4=IsExiting, 5=PollNextEvent, ...
    public static IntPtr Ptr;

    public static bool TrackedDeviceAdded(string serial, int deviceClass, IntPtr device)
    {
        var fn = (delegate* unmanaged<IntPtr, byte*, int, IntPtr, byte>)Vtbl.Slot(Ptr, 0);
        return fn(Ptr, Vtbl.StaticAnsi(serial), deviceClass, device) != 0;
    }

    public static void TrackedDevicePoseUpdated(uint objectId, ref DriverPose pose)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, DriverPose*, uint, void>)Vtbl.Slot(Ptr, 1);
        fixed (DriverPose* p = &pose) fn(Ptr, objectId, p, (uint)sizeof(DriverPose));
    }

    // vtable: 6=GetRawTrackedDevicePoses, 7=RequestRestart, 8=GetFrameTimings, 9=SetDisplayEyeToHead
    public static void SetDisplayEyeToHead(uint objectId, HmdMatrix34 left, HmdMatrix34 right)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, HmdMatrix34*, HmdMatrix34*, void>)Vtbl.Slot(Ptr, 9);
        fn(Ptr, objectId, &left, &right);
    }
}

/// <summary>IVRProperties_001(vrserver 提供)。</summary>
internal static unsafe class Properties
{
    // vtable: 0=ReadPropertyBatch, 1=WritePropertyBatch, 2=GetPropErrorNameFromEnum,
    // 3=TrackedDeviceToPropertyContainer
    public static IntPtr Ptr;

    public static ulong ContainerOf(uint deviceIndex)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, ulong>)Vtbl.Slot(Ptr, 3);
        return fn(Ptr, deviceIndex);
    }

    private static int Write(ulong container, int prop, uint tag, void* buffer, uint size)
    {
        var write = new PropertyWrite
        {
            Prop = prop,
            WriteType = VR.PropertyWrite_Set,
            Buffer = (IntPtr)buffer,
            BufferSize = size,
            Tag = tag,
        };
        var fn = (delegate* unmanaged<IntPtr, ulong, PropertyWrite*, uint, int>)Vtbl.Slot(Ptr, 1);
        int result = fn(Ptr, container, &write, 1);
        if (result != 0 || write.Error != 0)
            Log.Write($"WritePropertyBatch prop={prop} err={result}/{write.Error}");
        return result;
    }

    public static void SetString(ulong container, int prop, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value + "\0");
        fixed (byte* p = bytes) Write(container, prop, VR.StringPropertyTag, p, (uint)bytes.Length);
    }

    public static void SetFloat(ulong container, int prop, float value)
        => Write(container, prop, VR.FloatPropertyTag, &value, sizeof(float));

    public static void SetInt32(ulong container, int prop, int value)
        => Write(container, prop, VR.Int32PropertyTag, &value, sizeof(int));

    public static void SetUint64(ulong container, int prop, ulong value)
        => Write(container, prop, VR.Uint64PropertyTag, &value, sizeof(ulong));

    public static void SetBool(ulong container, int prop, bool value)
    {
        byte b = value ? (byte)1 : (byte)0;
        Write(container, prop, VR.BoolPropertyTag, &b, 1);
    }
}

/// <summary>IVRDriverInput_004(vrserver 提供)。</summary>
internal static unsafe class DriverInput
{
    // vtable: 0=CreateBooleanComponent, 1=UpdateBooleanComponent, 2=CreateScalarComponent,
    // 3=UpdateScalarComponent, 4=CreateHapticComponent, ...
    public static IntPtr Ptr;

    public static ulong CreateBoolean(ulong container, string name)
    {
        var fn = (delegate* unmanaged<IntPtr, ulong, byte*, ulong*, int>)Vtbl.Slot(Ptr, 0);
        ulong handle = 0;
        int err = fn(Ptr, container, Vtbl.StaticAnsi(name), &handle);
        if (err != 0) Log.Write($"CreateBooleanComponent {name} err={err}");
        return handle;
    }

    public static void UpdateBoolean(ulong component, bool value)
    {
        if (component == 0) return;
        var fn = (delegate* unmanaged<IntPtr, ulong, byte, double, int>)Vtbl.Slot(Ptr, 1);
        fn(Ptr, component, value ? (byte)1 : (byte)0, 0.0);
    }

    public static ulong CreateScalar(ulong container, string name, int units)
    {
        var fn = (delegate* unmanaged<IntPtr, ulong, byte*, ulong*, int, int, int>)Vtbl.Slot(Ptr, 2);
        ulong handle = 0;
        int err = fn(Ptr, container, Vtbl.StaticAnsi(name), &handle, VR.VRScalarType_Absolute, units);
        if (err != 0) Log.Write($"CreateScalarComponent {name} err={err}");
        return handle;
    }

    public static void UpdateScalar(ulong component, float value)
    {
        if (component == 0) return;
        var fn = (delegate* unmanaged<IntPtr, ulong, float, double, int>)Vtbl.Slot(Ptr, 3);
        fn(Ptr, component, value, 0.0);
    }

    public static ulong CreateHaptic(ulong container, string name)
    {
        var fn = (delegate* unmanaged<IntPtr, ulong, byte*, ulong*, int>)Vtbl.Slot(Ptr, 4);
        ulong handle = 0;
        int err = fn(Ptr, container, Vtbl.StaticAnsi(name), &handle);
        if (err != 0) Log.Write($"CreateHapticComponent {name} err={err}");
        return handle;
    }
}

/// <summary>IVRSettings_003(vrserver 提供)。</summary>
internal static unsafe class Settings
{
    // vtable: 0=GetSettingsErrorNameFromEnum, 1=SetBool, 2=SetInt32, 3=SetFloat, 4=SetString,
    // 5=GetBool, 6=GetInt32, 7=GetFloat, 8=GetString, 9=RemoveSection, 10=RemoveKeyInSection
    public static IntPtr Ptr;

    public static void SetBool(string section, string key, bool value)
    {
        if (Ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged<IntPtr, byte*, byte*, byte, int*, void>)Vtbl.Slot(Ptr, 1);
        int err = 0;
        fn(Ptr, Vtbl.StaticAnsi(section), Vtbl.StaticAnsi(key), value ? (byte)1 : (byte)0, &err);
        if (err != 0) Log.Write($"Settings.SetBool {section}.{key} err={err}");
    }

    public static void SetFloat(string section, string key, float value)
    {
        if (Ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged<IntPtr, byte*, byte*, float, int*, void>)Vtbl.Slot(Ptr, 3);
        int err = 0;
        fn(Ptr, Vtbl.StaticAnsi(section), Vtbl.StaticAnsi(key), value, &err);
        if (err != 0) Log.Write($"Settings.SetFloat {section}.{key} err={err}");
    }

    public static void SetInt32(string section, string key, int value)
    {
        if (Ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged<IntPtr, byte*, byte*, int, int*, void>)Vtbl.Slot(Ptr, 2);
        int err = 0;
        fn(Ptr, Vtbl.StaticAnsi(section), Vtbl.StaticAnsi(key), value, &err);
        if (err != 0) Log.Write($"Settings.SetInt32 {section}.{key} err={err}");
    }
}

/// <summary>IVRDriverLog_001(vrserver 提供)。</summary>
internal static unsafe class VRLog
{
    public static IntPtr Ptr;

    public static void Send(string message)
    {
        if (Ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged<IntPtr, byte*, void>)Vtbl.Slot(Ptr, 0);
        byte[] bytes = Encoding.ASCII.GetBytes(message + "\0");
        fixed (byte* p = bytes) fn(Ptr, p);
    }
}
