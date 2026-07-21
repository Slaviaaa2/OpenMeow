using System.Runtime.InteropServices;

namespace OpenMeow.Driver;

/// <summary>操作対象。コントロールパネル未接続時のフォールバック(F10 切替)でのみ使う。</summary>
internal enum ControlTarget { Head = 0, LeftHand = 1, RightHand = 2 }

/// <summary>片手分のボタン・トラックパッド状態のスナップショット。</summary>
internal struct HandButtons
{
    public bool Trigger, Grip, Menu, System, PadClick, PadTouch;
    public float PadX, PadY;
}

/// <summary>
/// キーボード+マウス入力から HMD とコントローラ2本の姿勢・ボタン状態を合成する。
/// 90Hz のポーズループ(<see cref="Provider"/>)から毎フレーム <see cref="Update"/> が呼ばれる。
///
/// 操作体系はモードレスのホールド式(押している間だけ意味が変わる):
/// <list type="bullet">
/// <item>マウス移動 = 視線。手の向きは視線上 <see cref="ConvergeDistance"/> の注視点へ収束+手首オフセット。</item>
/// <item>Space+マウス = 右手の位置 / 中クリック+マウス = 右手の手首(ホイール = ロール)。</item>
/// <item>X1(または左Alt)+マウス = 左手の位置 / X2(または左Alt+中クリック)= 左手の手首。</item>
/// <item>左手系ホールド中は LMB/RMB が左手のトリガー/グリップ(通常は右手)。</item>
/// <item>Tab / R +マウス = 左 / 右トラックパッドの仮想スティック(離すと中央へ戻る)。</item>
/// <item>Y / B = 右 / 左グリップ保持トグル。F5 / F6 = マウス左右 / 上下の反転トグル。</item>
/// </list>
/// 手は「構え位置+手動オフセット」のまま頭の向きに追従するだけで、自動で位置や姿勢を
/// 変えることはない(リセットは BackSpace のみ)。全ポーズは指数バネ補間で追従する。
/// </summary>
internal static class Simulation
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Virtual-Key codes
    private const int VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;
    private const int VK_BACK = 0x08, VK_TAB = 0x09;
    private const int VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21, VK_NEXT = 0x22; // PgUp / PgDn
    private const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private const int VK_F5 = 0x74, VK_F6 = 0x75;
    private const int VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79;
    private const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;

    // 移動・回転
    private const double MoveSpeed = 1.0;          // m/s
    private const double FastMultiplier = 2.5;
    private const double SlowMultiplier = 0.3;
    private const double TurnSpeed = 70.0 * Math.PI / 180.0; // rad/s
    private const double SmoothTau = 0.15;         // 移動入力のなめらか化 [s]
    private const double MouseSens = 0.15 * Math.PI / 180.0;  // 視線 rad/px
    private const double WristSens = 0.25 * Math.PI / 180.0;  // 手首 rad/px

    // 手
    private const double HandMouseSens = 0.0012;   // m/px(位置リーチ)
    private const double WheelDepthStep = 0.05;    // m/ノッチ(奥行き)
    private const double WheelRollStep = 10.0 * Math.PI / 180.0; // rad/ノッチ(手首ロール)
    private const double StickSens = 0.011;        // 仮想スティック 倒し量/px(~90px でフル)
    private const double StickReleaseTau = 0.06;   // スティック中央戻りの時定数 [s]
    private const double HandPoseTau = 0.09;       // 手ポーズのバネ時定数 [s]
    private const double ConvergeDistance = 2.0;   // 手のレーザーが視線と交わる距離 [m]

    /// <summary>入力を反映するかどうか。パネル接続時はマウスキャプチャ状態、未接続時は F9 トグル。</summary>
    public static volatile bool CaptureEnabled = true;

    /// <summary>フォールバック(パネル未接続)時の操作対象。</summary>
    public static ControlTarget Target { get; private set; } = ControlTarget.Head;

    // 頭
    private static double _headX, _headY = 1.6, _headZ;
    private static double _headYaw, _headPitch;

    /// <summary>手の状態。M*/YawOff/PitchOff = 手動オフセット、C* = バネ補間済み現在値(頭ローカル)。</summary>
    private struct Hand
    {
        public double Mx, My, Mz;
        public double YawOff, PitchOff, RollOff;
        public double Cx, Cy, Cz, CYaw, CPitch, CRoll;
        public bool Init;
    }

    private static Hand _left, _right;

    private static bool _prevF9, _prevF10;
    private static double _time;
    private static double _sx, _sy, _sz, _syaw, _spitch; // 平滑化済み移動入力
    private static double _stickX, _stickY;              // 仮想スティック(左パッド)の倒し
    private static double _stickRX, _stickRY;            // 仮想スティック(右パッド)の倒し
    private static bool _gripHoldL, _gripHoldR;          // グリップ保持トグル(B / Y)
    private static bool _prevB, _prevY;
    private static bool _invertX, _invertY;              // 方向反転(F5 / F6)
    private static bool _prevF5, _prevF6;

    /// <summary>直近の <see cref="Update"/> で合成された左手のボタン状態。</summary>
    public static HandButtons LeftButtons;

    /// <summary>直近の <see cref="Update"/> で合成された右手のボタン状態。</summary>
    public static HandButtons RightButtons;

    // 完全静止だと SteamVR が HMD を非アクティブ扱いするため、±1mm の微小揺れで常時「装着中」に見せる
    private static double JitterY => Math.Sin(_time * 2 * Math.PI * 0.7) * 0.001;

    /// <summary>入力を1フレーム分処理し、頭・両手の目標姿勢とボタン状態を更新する。</summary>
    /// <param name="dt">前回呼び出しからの経過秒。</param>
    public static void Update(double dt)
    {
        _time += dt;

        ControlSample link = ControlLink.Sample();
        if (link.Fresh)
        {
            if (CaptureEnabled != link.Active)
            {
                CaptureEnabled = link.Active;
                WriteState();
            }
        }
        else
        {
            bool f9 = Down(VK_F9);
            if (f9 && !_prevF9)
            {
                CaptureEnabled = !CaptureEnabled;
                Log.Write($"capture {(CaptureEnabled ? "ON" : "OFF")}");
                WriteState();
            }
            _prevF9 = f9;
        }

        if (!CaptureEnabled)
        {
            _prevF10 = Down(VK_F10);
            LeftButtons = default;
            RightButtons = default;
            UpdateHandPose(ref _left, -1, dt);
            UpdateHandPose(ref _right, +1, dt);
            return;
        }

        bool mouseFresh = link is { Fresh: true, Active: true };

        // パネル未接続のフォールバックだけ F10 の対象切替を残す
        if (!mouseFresh)
        {
            bool f10 = Down(VK_F10);
            if (f10 && !_prevF10)
            {
                Target = (ControlTarget)(((int)Target + 1) % 3);
                WriteState();
            }
            _prevF10 = f10;
        }

        // F5=左右反転 / F6=上下反転(視線・手首・手の位置に適用。スティックは対象外)
        bool f5 = Down(VK_F5);
        if (f5 && !_prevF5) { _invertX = !_invertX; Log.Write($"invertX={_invertX}"); WriteState(); }
        _prevF5 = f5;
        bool f6 = Down(VK_F6);
        if (f6 && !_prevF6) { _invertY = !_invertY; Log.Write($"invertY={_invertY}"); WriteState(); }
        _prevF6 = f6;
        double mdx = link.MouseDx * (_invertX ? -1 : 1);
        double mdy = link.MouseDy * (_invertY ? -1 : 1);

        // --- ホールド判定(モードレス)---
        bool leftMod = Down(VK_XBUTTON1) || Down(VK_LMENU);
        bool leftWrist = Down(VK_XBUTTON2) || (Down(VK_MBUTTON) && leftMod);
        bool leftPos = leftMod && !leftWrist;
        bool rightWrist = Down(VK_MBUTTON) && !leftMod;
        bool rightPos = Down(VK_SPACE) && !rightWrist;
        bool stick = Down(VK_TAB);  // 仮想スティック(左トラックパッド)
        bool stickR = Down('R');    // 仮想スティック(右トラックパッド)

        // マウスの行き先(優先: スティック > 左手首 > 左位置 > 右手首 > 右位置 > 視線)
        int route = stick ? 5 : stickR ? 6 : leftWrist ? 1 : leftPos ? 2 : rightWrist ? 3 : rightPos ? 4 : 0;

        // --- 移動(常に頭。EMA でなめらか加減速)---
        double mult = Down(VK_LSHIFT) ? FastMultiplier : Down(VK_LCONTROL) ? SlowMultiplier : 1.0;
        double speed = MoveSpeed * mult * dt;
        double turn = TurnSpeed * mult * dt;
        double alpha = Math.Min(1.0, dt / SmoothTau);
        _sx += (((Down('D') ? 1 : 0) - (Down('A') ? 1 : 0)) - _sx) * alpha;
        _sz += (((Down('S') ? 1 : 0) - (Down('W') ? 1 : 0)) - _sz) * alpha;
        _sy += (((Down('E') ? 1 : 0) - (Down('Q') ? 1 : 0)) - _sy) * alpha;
        _syaw += (((Down(VK_LEFT) ? 1 : 0) - (Down(VK_RIGHT) ? 1 : 0)) - _syaw) * alpha;
        _spitch += (((Down(VK_UP) ? 1 : 0) - (Down(VK_DOWN) ? 1 : 0)) - _spitch) * alpha;
        bool reset = Down(VK_BACK);

        {
            double sin = Math.Sin(_headYaw), cos = Math.Cos(_headYaw);
            _headX += (_sx * cos + _sz * sin) * speed;
            _headZ += (_sz * cos - _sx * sin) * speed;
            _headY += _sy * speed;
        }

        // 矢印は常に頭の微回転
        _headYaw += _syaw * turn;
        _headPitch = Math.Clamp(_headPitch + _spitch * turn, -1.5, 1.5);

        // --- マウスのルーティング ---
        if (route == 0)
        {
            if (mouseFresh)
            {
                _headYaw -= mdx * MouseSens;
                _headPitch = Math.Clamp(_headPitch - mdy * MouseSens, -1.5, 1.5);
            }
            if (reset) { _headX = 0; _headY = 1.6; _headZ = 0; _headYaw = 0; _headPitch = 0; _sx = _sy = _sz = 0; }
        }
        else if (route == 5)
        {
            // 仮想スティック: マウスの移動量 = 倒し量
            if (mouseFresh)
            {
                _stickX = Math.Clamp(_stickX + link.MouseDx * StickSens, -1, 1);
                _stickY = Math.Clamp(_stickY - link.MouseDy * StickSens, -1, 1);
            }
        }
        else if (route == 6)
        {
            if (mouseFresh)
            {
                _stickRX = Math.Clamp(_stickRX + link.MouseDx * StickSens, -1, 1);
                _stickRY = Math.Clamp(_stickRY - link.MouseDy * StickSens, -1, 1);
            }
        }
        else
        {
            ref Hand hand = ref (route is 1 or 2 ? ref _left : ref _right);
            if (mouseFresh)
            {
                if (route is 1 or 3)
                {
                    // 手首: マウスで向きを直接ぐりぐり、ホイールで横倒し(ロール)
                    hand.YawOff -= mdx * WristSens;
                    hand.PitchOff = Math.Clamp(hand.PitchOff - mdy * WristSens, -2.0, 2.0);
                    hand.RollOff += link.WheelDelta * WheelRollStep;
                }
                else
                {
                    // 位置: ビュー平面基準で動かす(見下ろし/見上げ時もマウスの方向=画面の方向)。
                    // ヨーフレームでの視線上ベクトル u=(0,cosφ,sinφ)、前方 f=(0,sinφ,−cosφ)。
                    double cp = Math.Cos(_headPitch), sp = Math.Sin(_headPitch);
                    hand.Mx += mdx * HandMouseSens;
                    hand.My += -mdy * cp * HandMouseSens;
                    hand.Mz += -mdy * sp * HandMouseSens;
                    hand.My += link.WheelDelta * sp * WheelDepthStep;
                    hand.Mz -= link.WheelDelta * cp * WheelDepthStep;
                }
            }
            {
                double cp = Math.Cos(_headPitch), sp = Math.Sin(_headPitch);
                double depth = ((Down(VK_PRIOR) ? 1 : 0) - (Down(VK_NEXT) ? 1 : 0)) * speed;
                hand.My += depth * sp;
                hand.Mz -= depth * cp;
            }
            if (reset) { hand.Mx = hand.My = hand.Mz = 0; hand.YawOff = hand.PitchOff = hand.RollOff = 0; }
        }

        // スティックを離したらスッと中央へ戻る
        if (!stick)
        {
            double sd = Math.Exp(-dt / StickReleaseTau);
            _stickX *= sd; _stickY *= sd;
            if (Math.Abs(_stickX) < 0.005) _stickX = 0;
            if (Math.Abs(_stickY) < 0.005) _stickY = 0;
        }
        if (!stickR)
        {
            double sd = Math.Exp(-dt / StickReleaseTau);
            _stickRX *= sd; _stickRY *= sd;
            if (Math.Abs(_stickRX) < 0.005) _stickRX = 0;
            if (Math.Abs(_stickRY) < 0.005) _stickRY = 0;
        }

        // --- ボタン ---
        LeftButtons = ReadHandButtons(
            trigger: 'Z', grip: 'X', menu: 'C', padClick: 'V',
            padUp: 'T', padLeft: 'F', padDown: 'G', padRight: 'H', system: VK_F7);
        RightButtons = ReadHandButtons(
            trigger: 'U', grip: 'O', menu: 'P', padClick: 'M',
            padUp: 'I', padLeft: 'J', padDown: 'K', padRight: 'L', system: VK_F8);

        // グリップ保持トグル: Y=右手 / B=左手(押すたびに保持⇔解除)
        bool bKey = Down('B');
        if (bKey && !_prevB) _gripHoldL = !_gripHoldL;
        _prevB = bKey;
        bool yKey = Down('Y');
        if (yKey && !_prevY) _gripHoldR = !_gripHoldR;
        _prevY = yKey;
        LeftButtons.Grip |= _gripHoldL;
        RightButtons.Grip |= _gripHoldR;

        // LMB/RMB: スティック中は LMB=左パッド押し込み。左手系ホールド中は左手、それ以外は右手
        if (mouseFresh)
        {
            if (stick)
            {
                LeftButtons.PadClick |= link.Lmb;
            }
            else if (stickR)
            {
                RightButtons.PadClick |= link.Lmb;
            }
            else
            {
                ref HandButtons hb = ref (leftMod ? ref LeftButtons : ref RightButtons);
                hb.Trigger |= link.Lmb;
                hb.Grip |= link.Rmb;
            }
        }

        // 仮想スティックをトラックパッドへ合成
        if (stick || _stickX != 0 || _stickY != 0)
        {
            LeftButtons.PadX = Math.Clamp(LeftButtons.PadX + (float)_stickX, -1f, 1f);
            LeftButtons.PadY = Math.Clamp(LeftButtons.PadY + (float)_stickY, -1f, 1f);
            LeftButtons.PadTouch = true;
        }
        if (stickR || _stickRX != 0 || _stickRY != 0)
        {
            RightButtons.PadX = Math.Clamp(RightButtons.PadX + (float)_stickRX, -1f, 1f);
            RightButtons.PadY = Math.Clamp(RightButtons.PadY + (float)_stickRY, -1f, 1f);
            RightButtons.PadTouch = true;
        }

        UpdateHandPose(ref _left, -1, dt);
        UpdateHandPose(ref _right, +1, dt);
    }

    private static void WriteState()
        => StateFile.Write(CaptureEnabled, Target, _invertX, _invertY);

    /// <summary>
    /// 手の現在ポーズを目標(構え位置+手動オフセット)へバネ補間で追従させる。
    /// 向きは視線上 <see cref="ConvergeDistance"/> の注視点へ収束させる:
    /// 手は頭からオフセットした位置にあるため、視線と平行に向けると
    /// 近距離ではレーザーが注視点から必ずずれてしまう。
    /// </summary>
    /// <param name="side">-1 = 左手、+1 = 右手。</param>
    private static void UpdateHandPose(ref Hand hand, int side, double dt)
    {
        double tx = 0.15 * side + hand.Mx;
        double ty = -0.30 + hand.My;
        double tz = -0.40 + hand.Mz;

        // 手のワールド位置(ヨーフレーム→ワールド)と視線上の注視点
        double cos = Math.Cos(_headYaw), sin = Math.Sin(_headYaw);
        double cp = Math.Cos(_headPitch), sp = Math.Sin(_headPitch);
        double wx = _headX + tx * cos + tz * sin;
        double wy = _headY + ty;
        double wz = _headZ + tz * cos - tx * sin;
        double px = _headX + (-cp * sin) * ConvergeDistance;
        double py = _headY + sp * ConvergeDistance;
        double pz = _headZ + (-cp * cos) * ConvergeDistance;
        double dx = px - wx, dy = py - wy, dz = pz - wz;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        double tyaw = Math.Atan2(-dx, -dz) + hand.YawOff;
        double tpitch = Math.Clamp(Math.Asin(dy / Math.Max(len, 0.01)) + hand.PitchOff, -2.0, 2.0);
        double troll = hand.RollOff;

        if (!hand.Init)
        {
            hand.Cx = tx; hand.Cy = ty; hand.Cz = tz; hand.CYaw = tyaw; hand.CPitch = tpitch; hand.CRoll = troll;
            hand.Init = true;
            return;
        }

        double a = Math.Min(1.0, dt / HandPoseTau);
        hand.Cx += (tx - hand.Cx) * a;
        hand.Cy += (ty - hand.Cy) * a;
        hand.Cz += (tz - hand.Cz) * a;
        hand.CYaw += WrapAngle(tyaw - hand.CYaw) * a;
        hand.CPitch += (tpitch - hand.CPitch) * a;
        hand.CRoll += WrapAngle(troll - hand.CRoll) * a;
    }

    private static double WrapAngle(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad < -Math.PI) rad += 2 * Math.PI;
        return rad;
    }

    private static HandButtons ReadHandButtons(
        int trigger, int grip, int menu, int padClick,
        int padUp, int padLeft, int padDown, int padRight, int system)
    {
        float x = (Down(padRight) ? 1 : 0) - (Down(padLeft) ? 1 : 0);
        float y = (Down(padUp) ? 1 : 0) - (Down(padDown) ? 1 : 0);
        bool click = Down(padClick);
        return new HandButtons
        {
            Trigger = Down(trigger),
            Grip = Down(grip),
            Menu = Down(menu),
            System = Down(system),
            PadClick = click,
            PadTouch = click || x != 0 || y != 0,
            PadX = x,
            PadY = y,
        };
    }

    // --- 姿勢の出力 ---

    private static HmdQuaternion YawPitch(double yaw, double pitch)
    {
        // q = qYaw(+Y軸) * qPitch(+X軸)
        double cy = Math.Cos(yaw / 2), sy = Math.Sin(yaw / 2);
        double cp = Math.Cos(pitch / 2), sp = Math.Sin(pitch / 2);
        return new HmdQuaternion
        {
            W = cy * cp,
            X = cy * sp,
            Y = sy * cp,
            Z = -sy * sp,
        };
    }

    private static HmdQuaternion YawPitchRoll(double yaw, double pitch, double roll)
    {
        // q = YawPitch(yaw,pitch) * qRoll(ローカルZ軸=手の指し示す軸まわりのひねり)
        HmdQuaternion q = YawPitch(yaw, pitch);
        double cr = Math.Cos(roll / 2), sr = Math.Sin(roll / 2);
        return new HmdQuaternion
        {
            W = q.W * cr - q.Z * sr,
            X = q.X * cr + q.Y * sr,
            Y = q.Y * cr - q.X * sr,
            Z = q.W * sr + q.Z * cr,
        };
    }

    private static unsafe DriverPose BasePose()
    {
        var pose = new DriverPose
        {
            QWorldFromDriverRotation = HmdQuaternion.Identity,
            QDriverFromHeadRotation = HmdQuaternion.Identity,
            Result = VR.TrackingResult_Running_OK,
            PoseIsValid = 1,
            DeviceIsConnected = 1,
        };
        return pose;
    }

    /// <summary>HMD の現在ポーズを DriverPose として返す。</summary>
    public static unsafe DriverPose HeadPose()
    {
        var pose = BasePose();
        pose.VecPosition[0] = _headX;
        pose.VecPosition[1] = _headY + JitterY;
        pose.VecPosition[2] = _headZ;
        pose.QRotation = YawPitch(_headYaw, _headPitch);
        return pose;
    }

    /// <summary>コントローラの現在ポーズを DriverPose として返す。</summary>
    public static unsafe DriverPose HandPose(bool isLeft)
    {
        Hand hand = isLeft ? _left : _right;
        double sin = Math.Sin(_headYaw), cos = Math.Cos(_headYaw);
        var pose = BasePose();
        // 移動系と同じ回転規約: x' = x·cos + z·sin, z' = z·cos − x·sin
        pose.VecPosition[0] = _headX + hand.Cx * cos + hand.Cz * sin;
        pose.VecPosition[1] = _headY + JitterY + hand.Cy;
        pose.VecPosition[2] = _headZ + hand.Cz * cos - hand.Cx * sin;
        pose.QRotation = YawPitchRoll(hand.CYaw, hand.CPitch, hand.CRoll);
        return pose;
    }
}
