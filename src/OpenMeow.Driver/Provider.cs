using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenMeow.Driver;

/// <summary>
/// ドライバのエントリポイント。<c>HmdDriverFactory</c> をネイティブエクスポートし、
/// <c>IServerTrackedDeviceProvider_004</c> を実装する(vrserver.exe から直接ロードされる)。
/// Init でデバイス3台を登録し、90Hz のポーズ送信スレッドとコントロールパネルを起動する。
/// </summary>
internal static unsafe class Provider
{
    private static IntPtr _providerObject;
    private static IntPtr _interfaceVersions;
    private static Thread? _poseThread;
    private static volatile bool _running;

    [UnmanagedCallersOnly(EntryPoint = "HmdDriverFactory")]
    public static IntPtr HmdDriverFactory(byte* interfaceName, int* returnCode)
    {
        string requested = Vtbl.ReadAnsi(interfaceName);
        if (requested == VR.IServerTrackedDeviceProvider_Version)
        {
            if (returnCode != null) *returnCode = VR.VRInitError_None;
            return GetProviderObject();
        }
        if (returnCode != null) *returnCode = VR.VRInitError_Init_InterfaceNotFound;
        return IntPtr.Zero;
    }

    private static IntPtr GetProviderObject()
    {
        if (_providerObject == IntPtr.Zero)
        {
            var vt = (IntPtr*)NativeMemory.Alloc(7 * (nuint)sizeof(IntPtr));
            vt[0] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&Init;
            vt[1] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&Cleanup;
            vt[2] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&GetInterfaceVersions;
            vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&RunFrame;
            vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, byte>)&ShouldBlockStandbyMode;
            vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&EnterStandby;
            vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&LeaveStandby;
            var obj = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            obj[0] = (IntPtr)vt;
            _providerObject = (IntPtr)obj;
        }
        return _providerObject;
    }

    [UnmanagedCallersOnly]
    private static int Init(IntPtr thisPtr, IntPtr driverContext)
    {
        Log.Init();
        Log.Write($"Init: DriverPose size={sizeof(DriverPose)} (expected 280)");

        DriverHost.Ptr = DriverContext.GetGenericInterface(driverContext, VR.IVRServerDriverHost_Version, out int hostErr);
        Properties.Ptr = DriverContext.GetGenericInterface(driverContext, VR.IVRProperties_Version, out int propErr);
        DriverInput.Ptr = DriverContext.GetGenericInterface(driverContext, VR.IVRDriverInput_Version, out int inputErr);
        VRLog.Ptr = DriverContext.GetGenericInterface(driverContext, VR.IVRDriverLog_Version, out _);
        Settings.Ptr = DriverContext.GetGenericInterface(driverContext, VR.IVRSettings_Version, out _);

        // 既定の turnOffScreensTimeout=5s では操作を止めるたびに HMD がスタンバイし、
        // 復帰時のウィンドウ前面化がゲームのフルスクリーンを解除してしまう。
        // 仮想 HMD にスタンバイは不要なので実質無効化する。
        Settings.SetFloat("power", "turnOffScreensTimeout", 86400f);
        Settings.SetBool("power", "pauseCompositorOnStandby", false);
        // シャペロン境界(青い壁)は仮想移動で常時表示されてしまうため非表示にする
        // (COLLISION_BOUNDS_STYLE_NONE = 4)
        Settings.SetInt32("collisionBounds", "CollisionBoundsStyle", 4);

        if (DriverHost.Ptr == IntPtr.Zero || Properties.Ptr == IntPtr.Zero || DriverInput.Ptr == IntPtr.Zero)
        {
            Log.Write($"Init failed: host={hostErr} props={propErr} input={inputErr}");
            return VR.VRInitError_Driver_Failed;
        }

        bool hmdAdded = DriverHost.TrackedDeviceAdded("OMEOW-HMD-001", VR.TrackedDeviceClass_HMD, Devices.Get(Devices.Hmd));
        bool leftAdded = DriverHost.TrackedDeviceAdded("OMEOW-CTL-L", VR.TrackedDeviceClass_Controller, Devices.Get(Devices.Left));
        bool rightAdded = DriverHost.TrackedDeviceAdded("OMEOW-CTL-R", VR.TrackedDeviceClass_Controller, Devices.Get(Devices.Right));
        Log.Write($"TrackedDeviceAdded hmd={hmdAdded} left={leftAdded} right={rightAdded}");

        _running = true;
        _poseThread = new Thread(PoseLoop) { IsBackground = true, Name = "OpenMeowPose" };
        _poseThread.Start();

        StateFile.Write(Simulation.CaptureEnabled, Simulation.Target);
        LaunchOverlay();
        return VR.VRInitError_None;
    }

    private static System.Diagnostics.Process? _overlay;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, char* fileName, uint size);

    /// <summary>
    /// この DLL 自身のディレクトリを返す。
    /// vrserver 内では AppContext.BaseDirectory がホスト exe 側を指すため使えない。
    /// </summary>
    private static string? DriverDirectory()
    {
        // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT
        if (!GetModuleHandleExW(0x4 | 0x2, (IntPtr)(delegate* unmanaged<IntPtr, void>)&Cleanup, out IntPtr module))
            return null;
        char* buffer = stackalloc char[512];
        uint len = GetModuleFileNameW(module, buffer, 512);
        return len == 0 ? null : Path.GetDirectoryName(new string(buffer, 0, (int)len));
    }

    private static void LaunchOverlay()
    {
        try
        {
            string? dir = DriverDirectory();
            if (dir == null) { Log.Write("overlay: driver dir unresolved"); return; }
            string exe = Path.Combine(dir, "OpenMeowOverlay.exe");
            if (!File.Exists(exe)) { Log.Write("overlay exe not found, skipping"); return; }
            if (System.Diagnostics.Process.GetProcessesByName("OpenMeowOverlay").Length > 0) return;
            _overlay = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true,
            });
            Log.Write("overlay launched");
        }
        catch (Exception ex)
        {
            Log.Write($"overlay launch failed: {ex.Message}");
        }
    }

    /// <summary>~90Hz で入力を処理し、全デバイスのポーズと入力コンポーネントを送信し続ける。</summary>
    private static void PoseLoop()
    {
        Log.Write("pose loop started");
        var clock = Stopwatch.StartNew();
        double previous = 0;
        while (_running)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(now - previous, 0.1);
            previous = now;

            try
            {
                Simulation.Update(dt);

                if (Devices.ObjectIds[Devices.Hmd] != VR.InvalidTrackedDeviceIndex)
                {
                    var pose = Simulation.HeadPose();
                    DriverHost.TrackedDevicePoseUpdated(Devices.ObjectIds[Devices.Hmd], ref pose);
                    DriverInput.UpdateBoolean(Devices.HmdProximity, true);
                }
                UpdateHand(Devices.Left, isLeft: true);
                UpdateHand(Devices.Right, isLeft: false);
            }
            catch (Exception ex)
            {
                Log.Write($"pose loop error: {ex.Message}");
            }

            Thread.Sleep(11); // ~90Hz
        }
        Log.Write("pose loop stopped");
    }

    private static void UpdateHand(int id, bool isLeft)
    {
        if (Devices.ObjectIds[id] == VR.InvalidTrackedDeviceIndex) return;

        var pose = Simulation.HandPose(isLeft);
        DriverHost.TrackedDevicePoseUpdated(Devices.ObjectIds[id], ref pose);

        HandButtons b = isLeft ? Simulation.LeftButtons : Simulation.RightButtons;
        ref Devices.HandComponents c = ref (isLeft ? ref Devices.LeftComponents : ref Devices.RightComponents);
        DriverInput.UpdateBoolean(c.SystemClick, b.System);
        DriverInput.UpdateBoolean(c.MenuClick, b.Menu);
        DriverInput.UpdateBoolean(c.GripClick, b.Grip);
        DriverInput.UpdateBoolean(c.TriggerClick, b.Trigger);
        DriverInput.UpdateScalar(c.TriggerValue, b.Trigger ? 1f : 0f);
        DriverInput.UpdateScalar(c.PadX, b.PadX);
        DriverInput.UpdateScalar(c.PadY, b.PadY);
        DriverInput.UpdateBoolean(c.PadClick, b.PadClick);
        DriverInput.UpdateBoolean(c.PadTouch, b.PadTouch);
    }

    [UnmanagedCallersOnly]
    private static void Cleanup(IntPtr thisPtr)
    {
        Log.Write("Cleanup");
        _running = false;
        _poseThread?.Join(500);
        _poseThread = null;
        try { if (_overlay is { HasExited: false }) _overlay.Kill(); } catch { }
        _overlay = null;
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetInterfaceVersions(IntPtr thisPtr)
    {
        if (_interfaceVersions == IntPtr.Zero)
        {
            var list = (IntPtr*)NativeMemory.Alloc(6 * (nuint)sizeof(IntPtr));
            list[0] = (IntPtr)Vtbl.StaticAnsi(VR.IVRSettings_Version);
            list[1] = (IntPtr)Vtbl.StaticAnsi(VR.ITrackedDeviceServerDriver_Version);
            list[2] = (IntPtr)Vtbl.StaticAnsi(VR.IVRDisplayComponent_Version);
            list[3] = (IntPtr)Vtbl.StaticAnsi(VR.IVRVirtualDisplay_Version);
            list[4] = (IntPtr)Vtbl.StaticAnsi(VR.IServerTrackedDeviceProvider_Version);
            list[5] = IntPtr.Zero;
            _interfaceVersions = (IntPtr)list;
        }
        return _interfaceVersions;
    }

    [UnmanagedCallersOnly]
    private static void RunFrame(IntPtr thisPtr) { }

    [UnmanagedCallersOnly]
    private static byte ShouldBlockStandbyMode(IntPtr thisPtr) => 0;

    [UnmanagedCallersOnly]
    private static void EnterStandby(IntPtr thisPtr) { }

    [UnmanagedCallersOnly]
    private static void LeaveStandby(IntPtr thisPtr) { }
}
