namespace OpenMeow.Driver;

/// <summary>
/// %LOCALAPPDATA%\OpenMeow\openmeow_driver.log へのファイルログと、
/// SteamVR 側ログ(vrserver.txt)への IVRDriverLog 転送。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Init()
    {
        lock (Gate)
        {
            if (_writer != null) return;
            try
            {
                // vrserver プロセス内で動くため BaseDirectory は SteamVR の bin を指す。ユーザー領域に書く。
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMeow");
                Directory.CreateDirectory(dir);
                _writer = new StreamWriter(Path.Combine(dir, "openmeow_driver.log"), append: false) { AutoFlush = true };
            }
            catch
            {
                // vrserver から書き込めない場所なら黙ってファイルログなしで動く
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"); }
            catch { }
        }
        VRLog.Send($"[openmeow] {message}");
    }
}

/// <summary>オーバーレイアプリへ現在状態を伝えるファイル(%LOCALAPPDATA%\OpenMeow\state.txt)。</summary>
internal static class StateFile
{
    private static string? _path;

    public static void Write(bool capture, ControlTarget target, bool invertX = false, bool invertY = false)
    {
        try
        {
            if (_path == null)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMeow");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "state.txt");
            }
            string temp = _path + ".tmp";
            File.WriteAllText(temp,
                $"capture={(capture ? 1 : 0)}\ntarget={(int)target}\ninvertX={(invertX ? 1 : 0)}\ninvertY={(invertY ? 1 : 0)}\n");
            File.Move(temp, _path, overwrite: true);
        }
        catch { }
    }
}
