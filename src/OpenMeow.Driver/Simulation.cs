using System.Runtime.InteropServices;
using OpenMeow;

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
/// <item>マウス移動 = 視線。手の向きは視線上 <see cref="ConvergeDistance"/> の注視点へ収束+腕の傾き。</item>
/// <item>左Shift+マウス = 右腕の位置 / 左Shift+中クリック+マウス = 右腕の傾き(中クリック単独も可)。</item>
/// <item>左Ctrl+マウス = 左腕の位置 / 左Ctrl+中クリック+マウス = 左腕の傾き。</item>
/// <item>腕の傾きは肩を支点に手の位置と向きを一緒に回し、横ドラッグで横倒し、縦ドラッグで上下に振る。</item>
/// <item>X1 / X2 は左腕位置 / 左腕傾きを直接選ぶマウスショートカット。</item>
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

    // 移動・回転
    private const double MoveSpeed = 1.0;          // m/s
    private const double FastMultiplier = 2.5;
    private const double SlowMultiplier = 0.3;
    private const double TurnSpeed = 70.0 * Math.PI / 180.0; // rad/s
    private const double SmoothTau = 0.15;         // 移動入力のなめらか化 [s]
    private const double MouseSens = 0.15 * Math.PI / 180.0;  // 視線 rad/px
    private const double ArmTiltSens = 0.25 * Math.PI / 180.0; // 腕の肩支点回転 rad/px

    // 手
    private const double HandMouseSens = 0.0012;   // m/px(位置リーチ)
    private const double WheelDepthStep = 0.05;    // m/ノッチ(奥行き)
    private const double WheelYawStep = 10.0 * Math.PI / 180.0; // rad/ノッチ(腕の向き)
    private const double StickSens = 0.011;        // 仮想スティック 倒し量/px(~90px でフル)
    private const double StickReleaseTau = 0.06;   // スティック中央戻りの時定数 [s]
    private const double ConvergeDistance = 2.0;   // 手のレーザーが視線と交わる距離 [m]

    /// <summary>入力を反映するかどうか。パネル接続時はマウスキャプチャ状態、未接続時は F9 トグル。</summary>
    public static volatile bool CaptureEnabled = true;

    /// <summary>フォールバック(パネル未接続)時の操作対象。</summary>
    public static ControlTarget Target { get; private set; } = ControlTarget.Head;

    // 頭
    private static double _headX, _headY = 1.6, _headZ;
    private static double _headYaw, _headPitch;

    /// <summary>手の状態。M* = 位置、Arm* = 肩支点の腕回転、YawOff = 向き、C* = 補間済み現在値。</summary>
    private struct Hand
    {
        public double Mx, My, Mz;
        public double ArmSideOff, ArmLiftOff, YawOff;
        public double Cx, Cy, Cz, CYaw, CPitch, CRoll;
        public double Vx, Vy, Vz, VYaw, VPitch, VRoll;
        public double PrevTx, PrevTy, PrevTz, PrevTYaw, PrevTPitch, PrevTRoll;
        public bool HasTarget;
        public bool Init;
    }

    private struct TrackerState
    {
        public double X, Y, Z;
        public double Vx, Vy, Vz;
        public double VYaw;
        public double Yaw, Pitch, Roll;
        public bool Initialized;
    }

    private static Hand _left, _right;
    private static TrackerState _waist, _leftFoot, _rightFoot;
    private static readonly BodyGaitKernel _bodyGait = new();
    private static bool _bodyTrackersEnabled;

    private static bool _prevF9, _prevF10;
    private static double _time;
    private static double _sx, _sy, _sz, _syaw, _spitch; // 平滑化済み移動入力
    private static double _stickX, _stickY;              // 仮想スティック(左パッド)の倒し
    private static double _stickRX, _stickRY;            // 仮想スティック(右パッド)の倒し
    private static bool _gripHoldL, _gripHoldR;          // グリップ保持トグル(B / Y)
    private static bool _prevB, _prevY;
    private static bool _invertX, _invertY;              // 方向反転(F5 / F6)
    private static bool _prevF5, _prevF6;
    private static KeyBindings _keys = KeyBindings.Defaults();
    private static DateTime _keyConfigChecked = DateTime.MinValue;
    private static DesktopMotionSettings _motion = DesktopMotionSettings.Legacy();
    private static DateTime _motionConfigChecked = DateTime.MinValue;

    /// <summary>直近の <see cref="Update"/> で合成された左手のボタン状態。</summary>
    public static HandButtons LeftButtons;

    /// <summary>直近の <see cref="Update"/> で合成された右手のボタン状態。</summary>
    public static HandButtons RightButtons;

    // 完全静止だと SteamVR が HMD を非アクティブ扱いするため、±1mm の微小揺れで常時「装着中」に見せる
    private static double JitterY => Math.Sin(_time * 2 * Math.PI * 0.7) * 0.001;

    /// <summary>SteamVR セッション開始時に固定する GenericTracker トポロジー。</summary>
    public static void ConfigureBodyTrackers(DesktopMotionSettings settings)
    {
        DesktopMotionSettings configured = settings.Clone();
        configured.Sanitize();
        _motion = configured;
        _bodyTrackersEnabled = configured.EnableBodyTrackers;
        ConfigureBodyGait(configured);
        ResetBodyDynamics();
    }

    /// <summary>入力を1フレーム分処理し、頭・両手の目標姿勢とボタン状態を更新する。</summary>
    /// <param name="dt">前回呼び出しからの経過秒。</param>
    public static void Update(double dt)
    {
        dt = !double.IsFinite(dt) || dt <= 0
            ? 1.0 / 90.0
            : Math.Clamp(dt, 0.0001, 0.1);
        _time += dt;
        RefreshKeyBindings();
        RefreshMotionSettings();

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
            bool f9 = Down("CaptureToggle");
            if (f9 && !_prevF9)
            {
                CaptureEnabled = !CaptureEnabled;
                Log.Write($"capture {(CaptureEnabled ? "ON" : "OFF")}");
                WriteState();
            }
            _prevF9 = f9;
        }

        bool reset = Down("Reset");
        if (!CaptureEnabled)
        {
            _prevF10 = Down("TargetNext");
            LeftButtons = default;
            RightButtons = default;
            UpdateHandPose(ref _left, -1, dt);
            UpdateHandPose(ref _right, +1, dt);
            UpdateBodyGait(dt, reset);
            return;
        }

        bool mouseFresh = link is { Fresh: true, Active: true };

        // パネル未接続のフォールバックだけ F10 の対象切替を残す
        if (!mouseFresh)
        {
            bool f10 = Down("TargetNext");
            if (f10 && !_prevF10)
            {
                Target = (ControlTarget)(((int)Target + 1) % 3);
                WriteState();
            }
            _prevF10 = f10;
        }

        // F5=左右反転 / F6=上下反転(視線・腕の傾き・手の位置に適用。スティックは対象外)
        bool f5 = Down("InvertX");
        if (f5 && !_prevF5) { _invertX = !_invertX; Log.Write($"invertX={_invertX}"); WriteState(); }
        _prevF5 = f5;
        bool f6 = Down("InvertY");
        if (f6 && !_prevF6) { _invertY = !_invertY; Log.Write($"invertY={_invertY}"); WriteState(); }
        _prevF6 = f6;
        double mdx = link.MouseDx * (_invertX ? -1 : 1);
        double mdy = link.MouseDy * (_invertY ? -1 : 1);

        // --- ホールド判定(モードレス)---
        bool leftMod = Down("LeftPosition") || Down("LeftPositionAlt");
        bool leftWrist = Down("LeftWrist") || (Down("LeftWristMouse") && leftMod);
        bool leftPos = leftMod && !leftWrist;
        bool rightWrist = Down("RightWrist") && !leftMod;
        bool rightPos = Down("RightPosition") && !rightWrist;
        bool stick = Down("LeftStick");
        bool stickR = Down("RightStick");

        // マウスの行き先(優先: スティック > 左腕傾き > 左位置 > 右腕傾き > 右位置 > 視線)
        int route = stick ? 5 : stickR ? 6 : leftWrist ? 1 : leftPos ? 2 : rightWrist ? 3 : rightPos ? 4 : 0;

        // --- 移動(常に頭。EMA でなめらか加減速)---
        double mult = Down("Fast") ? _motion.FastMultiplier : Down("Slow") ? _motion.SlowMultiplier : 1.0;
        double speed = _motion.MovementSpeed * mult * dt;
        double turn = _motion.TurnDegreesPerSecond * Math.PI / 180.0 * mult * dt;
        double alpha = Math.Min(1.0, dt / _motion.MovementSmoothTau);
        _sx += (((Down("MoveRight") ? 1 : 0) - (Down("MoveLeft") ? 1 : 0)) - _sx) * alpha;
        _sz += (((Down("MoveBack") ? 1 : 0) - (Down("MoveForward") ? 1 : 0)) - _sz) * alpha;
        _sy += (((Down("MoveUp") ? 1 : 0) - (Down("MoveDown") ? 1 : 0)) - _sy) * alpha;
        _syaw += (((Down("TurnLeft") ? 1 : 0) - (Down("TurnRight") ? 1 : 0)) - _syaw) * alpha;
        _spitch += (((Down("LookUp") ? 1 : 0) - (Down("LookDown") ? 1 : 0)) - _spitch) * alpha;
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
                double mouseSens = _motion.MouseSensitivityDegreesPerPixel * Math.PI / 180.0;
                _headYaw -= mdx * mouseSens;
                _headPitch = Math.Clamp(_headPitch - mdy * mouseSens, -1.5, 1.5);
            }
            if (reset) { _headX = 0; _headY = 1.6; _headZ = 0; _headYaw = 0; _headPitch = 0; _sx = _sy = _sz = 0; }
        }
        else if (route == 5)
        {
            // 仮想スティック: マウスの移動量 = 倒し量
            if (mouseFresh)
            {
                _stickX = Math.Clamp(_stickX + link.MouseDx * _motion.StickSensitivity, -1, 1);
                _stickY = Math.Clamp(_stickY - link.MouseDy * _motion.StickSensitivity, -1, 1);
            }
        }
        else if (route == 6)
        {
            if (mouseFresh)
            {
                _stickRX = Math.Clamp(_stickRX + link.MouseDx * _motion.StickSensitivity, -1, 1);
                _stickRY = Math.Clamp(_stickRY - link.MouseDy * _motion.StickSensitivity, -1, 1);
            }
        }
        else
        {
            ref Hand hand = ref (route is 1 or 2 ? ref _left : ref _right);
            if (mouseFresh)
            {
                if (route is 1 or 3)
                {
                    // 腕全体の傾き: 肩を支点に位置と向きを一緒に回す。
                    // 横ドラッグで横倒し、縦ドラッグで前後方向へ上げ下げする。
                    // 斜めドラッグでは両軸が同時に動く。ホイールは手先の向きを左右へ振る。
                    double armSens = _motion.ArmSensitivityDegreesPerPixel * Math.PI / 180.0;
                    hand.ArmSideOff = Math.Clamp(
                        hand.ArmSideOff + mdx * armSens, -1.45, 1.45);
                    hand.ArmLiftOff = Math.Clamp(
                        hand.ArmLiftOff - mdy * armSens, -1.6, 1.6);
                    hand.YawOff += link.WheelDelta * _motion.WheelYawDegreesPerNotch * Math.PI / 180.0;
                }
                else
                {
                    // 位置: ビュー平面基準で動かす(見下ろし/見上げ時もマウスの方向=画面の方向)。
                    // ヨーフレームでの視線上ベクトル u=(0,cosφ,sinφ)、前方 f=(0,sinφ,−cosφ)。
                    double cp = Math.Cos(_headPitch), sp = Math.Sin(_headPitch);
                    hand.Mx += mdx * _motion.HandPositionSensitivity;
                    hand.My += -mdy * cp * _motion.HandPositionSensitivity;
                    hand.Mz += -mdy * sp * _motion.HandPositionSensitivity;
                    hand.My += link.WheelDelta * sp * _motion.WheelDepthStep;
                    hand.Mz -= link.WheelDelta * cp * _motion.WheelDepthStep;
                }
            }
            {
                double cp = Math.Cos(_headPitch), sp = Math.Sin(_headPitch);
                double depth = ((Down("DepthForward") ? 1 : 0) - (Down("DepthBack") ? 1 : 0)) * speed;
                hand.My += depth * sp;
                hand.Mz -= depth * cp;
            }
            if (reset)
            {
                hand.Mx = hand.My = hand.Mz = 0;
                hand.ArmSideOff = hand.ArmLiftOff = hand.YawOff = 0;
                ResetHandDynamics(ref hand);
            }
        }

        // スティックを離したらスッと中央へ戻る
        if (!stick)
        {
            double sd = Math.Exp(-dt / _motion.StickReleaseTau);
            _stickX *= sd; _stickY *= sd;
            if (Math.Abs(_stickX) < 0.005) _stickX = 0;
            if (Math.Abs(_stickY) < 0.005) _stickY = 0;
        }
        if (!stickR)
        {
            double sd = Math.Exp(-dt / _motion.StickReleaseTau);
            _stickRX *= sd; _stickRY *= sd;
            if (Math.Abs(_stickRX) < 0.005) _stickRX = 0;
            if (Math.Abs(_stickRY) < 0.005) _stickRY = 0;
        }

        // --- ボタン ---
        LeftButtons = ReadHandButtons(
            trigger: "LeftTrigger", grip: "LeftGrip", menu: "LeftMenu", padClick: "LeftPadClick",
            padUp: "LeftPadUp", padLeft: "LeftPadLeft", padDown: "LeftPadDown", padRight: "LeftPadRight", system: "LeftSystem");
        RightButtons = ReadHandButtons(
            trigger: "RightTrigger", grip: "RightGrip", menu: "RightMenu", padClick: "RightPadClick",
            padUp: "RightPadUp", padLeft: "RightPadLeft", padDown: "RightPadDown", padRight: "RightPadRight", system: "RightSystem");

        // グリップ保持トグル: Y=右手 / B=左手(押すたびに保持⇔解除)
        bool bKey = Down("GripHoldLeft");
        if (bKey && !_prevB) _gripHoldL = !_gripHoldL;
        _prevB = bKey;
        bool yKey = Down("GripHoldRight");
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
        UpdateBodyGait(dt, reset);
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
        // 頭ローカルの仮想肩を支点に、手までのベクトルを腕操作で回す。
        // 位置だけでなくコントローラー姿勢も同じ角度だけ傾けることで、
        // アバター側 IK に腕全体を横倒し / 上下させたポーズとして伝える。
        double shoulderX = 0.18 * side;
        const double shoulderY = -0.18;
        const double shoulderZ = -0.05;
        double armX = -0.03 * side;
        const double armY = -0.12;
        const double armZ = -0.35;

        double liftCos = Math.Cos(hand.ArmLiftOff), liftSin = Math.Sin(hand.ArmLiftOff);
        double liftedY = armY * liftCos - armZ * liftSin;
        double liftedZ = armY * liftSin + armZ * liftCos;
        double sideCos = Math.Cos(hand.ArmSideOff), sideSin = Math.Sin(hand.ArmSideOff);
        double tiltedX = armX * sideCos - liftedY * sideSin;
        double tiltedY = armX * sideSin + liftedY * sideCos;

        // 手動の位置オフセットは回転後に足し、腕を傾けた後も
        // マウスによる画面上下左右 / 奥行き操作の方向が変わらないようにする。
        double tx = shoulderX + tiltedX + hand.Mx;
        double ty = shoulderY + tiltedY + hand.My;
        double tz = shoulderZ + liftedZ + hand.Mz;

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
        double tpitch = Math.Clamp(
            Math.Asin(dy / Math.Max(len, 0.01)) + hand.ArmLiftOff, -2.0, 2.0);
        double troll = hand.ArmSideOff;

        if (!hand.Init)
        {
            hand.Cx = tx; hand.Cy = ty; hand.Cz = tz; hand.CYaw = tyaw; hand.CPitch = tpitch; hand.CRoll = troll;
            ResetHandDynamics(ref hand);
            hand.PrevTx = tx; hand.PrevTy = ty; hand.PrevTz = tz;
            hand.PrevTYaw = tyaw; hand.PrevTPitch = tpitch; hand.PrevTRoll = troll;
            hand.HasTarget = true;
            hand.Init = true;
            return;
        }

        if (_motion.HandSmoothingMode == MotionSmoothingMode.Legacy)
        {
            // The Legacy preset still resolves to the historical 0.09 s value,
            // while an explicitly edited legacy config can tune this setting.
            double a = Math.Min(1.0, dt / _motion.HandPoseTau);
            hand.Cx += (tx - hand.Cx) * a;
            hand.Cy += (ty - hand.Cy) * a;
            hand.Cz += (tz - hand.Cz) * a;
            hand.CYaw += WrapAngle(tyaw - hand.CYaw) * a;
            hand.CPitch += (tpitch - hand.CPitch) * a;
            hand.CRoll += WrapAngle(troll - hand.CRoll) * a;
            ResetHandDynamics(ref hand);
        }
        else
        {
            double step = Math.Clamp(dt, 0.0001, 0.1);
            double prediction = Math.Clamp(_motion.HandPredictionSeconds, 0.0, 0.15);
            double maxSpeed = Math.Max(0.05, _motion.HandMaxSpeed);
            double maxAcceleration = Math.Max(0.1, _motion.HandMaxAcceleration);
            double omega = 2.0 * Math.PI * Math.Max(1.0, _motion.HandSpringHz);
            double damping = Math.Max(1.0, _motion.HandDamping);

            double txVelocity = hand.HasTarget ? Math.Clamp((tx - hand.PrevTx) / step, -maxSpeed, maxSpeed) : 0;
            double tyVelocity = hand.HasTarget ? Math.Clamp((ty - hand.PrevTy) / step, -maxSpeed, maxSpeed) : 0;
            double tzVelocity = hand.HasTarget ? Math.Clamp((tz - hand.PrevTz) / step, -maxSpeed, maxSpeed) : 0;
            double yawVelocity = hand.HasTarget ? Math.Clamp(WrapAngle(tyaw - hand.PrevTYaw) / step, -maxSpeed, maxSpeed) : 0;
            double pitchVelocity = hand.HasTarget ? Math.Clamp((tpitch - hand.PrevTPitch) / step, -maxSpeed, maxSpeed) : 0;
            double rollVelocity = hand.HasTarget ? Math.Clamp(WrapAngle(troll - hand.PrevTRoll) / step, -maxSpeed, maxSpeed) : 0;

            hand.Cx = SecondOrderStep(hand.Cx, ref hand.Vx, tx + txVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration);
            hand.Cy = SecondOrderStep(hand.Cy, ref hand.Vy, ty + tyVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration);
            hand.Cz = SecondOrderStep(hand.Cz, ref hand.Vz, tz + tzVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration);
            hand.CYaw = WrapAngle(SecondOrderStep(hand.CYaw, ref hand.VYaw, hand.CYaw + WrapAngle(tyaw - hand.CYaw) + yawVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration));
            hand.CPitch = SecondOrderStep(hand.CPitch, ref hand.VPitch, tpitch + pitchVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration);
            hand.CRoll = WrapAngle(SecondOrderStep(hand.CRoll, ref hand.VRoll, hand.CRoll + WrapAngle(troll - hand.CRoll) + rollVelocity * prediction, step, omega, damping, maxSpeed, maxAcceleration));
        }

        hand.PrevTx = tx; hand.PrevTy = ty; hand.PrevTz = tz;
        hand.PrevTYaw = tyaw; hand.PrevTPitch = tpitch; hand.PrevTRoll = troll;
        hand.HasTarget = true;
    }

    private static double SecondOrderStep(double value, ref double velocity, double target,
        double dt, double omega, double damping, double maxSpeed, double maxAcceleration)
    {
        double acceleration = omega * omega * (target - value) - 2.0 * damping * omega * velocity;
        acceleration = Math.Clamp(acceleration, -maxAcceleration, maxAcceleration);
        velocity = Math.Clamp(velocity + acceleration * dt, -maxSpeed, maxSpeed);
        return value + velocity * dt;
    }

    private static void ResetHandDynamics(ref Hand hand)
    {
        hand.Vx = hand.Vy = hand.Vz = hand.VYaw = hand.VPitch = hand.VRoll = 0;
        hand.HasTarget = false;
    }

    private static double WrapAngle(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad < -Math.PI) rad += 2 * Math.PI;
        return rad;
    }

    private static HandButtons ReadHandButtons(
        string trigger, string grip, string menu, string padClick,
        string padUp, string padLeft, string padDown, string padRight, string system)
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

    private static bool Down(string id) => Down(_keys.Get(id));

    private static void RefreshKeyBindings()
    {
        if (DateTime.UtcNow - _keyConfigChecked < TimeSpan.FromSeconds(1)) return;
        _keyConfigChecked = DateTime.UtcNow;
        _keys = KeyBindings.LoadOrDefault();
    }

    private static void RefreshMotionSettings()
    {
        if (DateTime.UtcNow - _motionConfigChecked < TimeSpan.FromSeconds(1)) return;
        _motionConfigChecked = DateTime.UtcNow;
        DesktopMotionSettings loaded = DesktopMotionSettings.LoadOrDefault();
        // Topology is fixed for the running SteamVR provider. Keep a saved
        // checkbox change from dynamically adding/removing devices, while
        // allowing numeric gait tuning to settle in on the next poll.
        loaded.EnableBodyTrackers = _bodyTrackersEnabled;
        if (MotionSettingsEqual(_motion, loaded)) return;
        _motion = loaded;
        ConfigureBodyGait(loaded);
        ResetHandDynamics(ref _left);
        ResetHandDynamics(ref _right);
        ResetBodyDynamics();
    }

    private static void ConfigureBodyGait(DesktopMotionSettings settings)
        => _bodyGait.Configure(new BodyGaitParameters(
            settings.BodyHeightMeters, settings.HipFollowTau, settings.HipLeanDegrees,
            settings.FootSpacingMeters, settings.StrideLengthMeters, settings.StepHeightMeters,
            settings.GaitSmoothingTau, settings.TurnToeDegrees, settings.FootPlantStrength));

    private static bool MotionSettingsEqual(DesktopMotionSettings a, DesktopMotionSettings b)
        => string.Equals(a.Preset, b.Preset, StringComparison.OrdinalIgnoreCase)
            && a.MovementSpeed == b.MovementSpeed && a.FastMultiplier == b.FastMultiplier
            && a.SlowMultiplier == b.SlowMultiplier && a.TurnDegreesPerSecond == b.TurnDegreesPerSecond
            && a.MovementSmoothTau == b.MovementSmoothTau
            && a.MouseSensitivityDegreesPerPixel == b.MouseSensitivityDegreesPerPixel
            && a.ArmSensitivityDegreesPerPixel == b.ArmSensitivityDegreesPerPixel
            && a.HandPositionSensitivity == b.HandPositionSensitivity && a.WheelDepthStep == b.WheelDepthStep
            && a.WheelYawDegreesPerNotch == b.WheelYawDegreesPerNotch && a.StickSensitivity == b.StickSensitivity
            && a.StickReleaseTau == b.StickReleaseTau && a.HandPoseTau == b.HandPoseTau
            && a.HandSmoothingMode == b.HandSmoothingMode && a.HandSpringHz == b.HandSpringHz
            && a.HandDamping == b.HandDamping && a.HandMaxSpeed == b.HandMaxSpeed
            && a.HandMaxAcceleration == b.HandMaxAcceleration && a.HandPredictionSeconds == b.HandPredictionSeconds
            && a.EnableBodyTrackers == b.EnableBodyTrackers && a.BodyHeightMeters == b.BodyHeightMeters
            && a.HipFollowTau == b.HipFollowTau && a.HipLeanDegrees == b.HipLeanDegrees
            && a.FootSpacingMeters == b.FootSpacingMeters && a.StrideLengthMeters == b.StrideLengthMeters
            && a.StepHeightMeters == b.StepHeightMeters && a.GaitSmoothingTau == b.GaitSmoothingTau
            && a.TurnToeDegrees == b.TurnToeDegrees && a.FootPlantStrength == b.FootPlantStrength;

    private static void ResetBodyDynamics()
    {
        _bodyGait.Reset(new BodyGaitHeadPose(_headX, _headY, _headZ, _headYaw));
        _waist = default;
        _leftFoot = default;
        _rightFoot = default;
    }

    /// <summary>
    /// Deterministic waist/foot gait from the actual HMD horizontal trajectory.
    /// Feet use a continuous smoothstep stride and a sinusoidal swing lift;
    /// stance blending keeps the planted foot in world space without jumps.
    /// </summary>
    private static void UpdateBodyGait(double dt, bool reset)
    {
        if (!_bodyTrackersEnabled) return;
        BodyGaitFrame frame = _bodyGait.Step(dt, new BodyGaitHeadPose(_headX, _headY, _headZ, _headYaw), reset);
        CopyTrackerPose(frame.Waist, ref _waist);
        CopyTrackerPose(frame.LeftFoot, ref _leftFoot);
        CopyTrackerPose(frame.RightFoot, ref _rightFoot);
    }

    private static void CopyTrackerPose(BodyGaitTrackerPose source, ref TrackerState destination)
    {
        destination.X = source.X; destination.Y = source.Y; destination.Z = source.Z;
        destination.Vx = source.Vx; destination.Vy = source.Vy; destination.Vz = source.Vz;
        destination.VYaw = source.AngularVelocityYaw;
        destination.Yaw = source.Yaw; destination.Pitch = source.Pitch; destination.Roll = source.Roll;
        destination.Initialized = source.Initialized;
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

    /// <summary>腰・左足・右足 GenericTracker の現在ポーズ。</summary>
    public static unsafe DriverPose TrackerPose(int id)
    {
        TrackerState tracker = id switch
        {
            Devices.Waist => _waist,
            Devices.LeftFoot => _leftFoot,
            Devices.RightFoot => _rightFoot,
            _ => default,
        };
        var pose = BasePose();
        pose.VecPosition[0] = Finite(tracker.X);
        pose.VecPosition[1] = Finite(tracker.Y);
        pose.VecPosition[2] = Finite(tracker.Z);
        pose.VecVelocity[0] = Finite(tracker.Vx);
        pose.VecVelocity[1] = Finite(tracker.Vy);
        pose.VecVelocity[2] = Finite(tracker.Vz);
        pose.VecAngularVelocity[1] = Finite(tracker.VYaw);
        pose.QRotation = YawPitchRoll(Finite(tracker.Yaw), Finite(tracker.Pitch), Finite(tracker.Roll));
        return pose;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
