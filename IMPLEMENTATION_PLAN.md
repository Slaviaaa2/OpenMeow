# OpenMeow 実装計画

キーボードで VR ゲームを操作するための SteamVR 仮想デバイスドライバ。
.NET 10 NativeAOT で C++ を使わずにネイティブ SteamVR ドライバ DLL を作る。

## アーキテクチャ

- `src/OpenMeow.Driver` — NativeAOT (`NativeLib=Shared`, win-x64) でビルドされる
  `driver_openmeow.dll`。`HmdDriverFactory` を `UnmanagedCallersOnly` でエクスポート。
- OpenVR ドライバ API は C++ 仮想クラスなので、vtable をアンマネージドメモリに手動構築して
  相互運用する(呼ばれる側)。vrserver 側インターフェースは vtable スロットを
  `delegate* unmanaged` で直接呼ぶ(呼ぶ側)。
- 提供デバイス: 仮想 HMD ×1(拡張モードの仮想ディスプレイ)+ Vive ワンドエミュレーション
  コントローラ ×2(`{htc}/input/vive_controller_profile.json` を流用)。
- キーボード入力はドライバ自身(vrserver プロセス内)が `GetAsyncKeyState` で
  グローバル監視。90Hz の背景スレッドで姿勢と入力コンポーネントを更新。

## ABI 上の注意(x64 MSVC)

- C++ メンバ関数 = this を第1引数とする標準 x64 呼び出し規約。
- 値返しの大きい構造体(`ComputeDistortion` の `DistortionCoordinates_t`)は
  this の次(RDX)に隠し戻り値ポインタ → C# 側は
  `IntPtr Fn(IntPtr thisPtr, Ret* ret, ...)` として ret を返す。
- C++ `bool` は 1 バイト → C# では `byte`。

## 検証済みインターフェース(openvr_driver.h master, 2026-07 取得)

| インターフェース | バージョン | 用途 |
|---|---|---|
| IServerTrackedDeviceProvider | _004 | 実装(エントリポイント) |
| ITrackedDeviceServerDriver | _005 | 実装(HMD/コントローラ、GetPose は呼ばれない) |
| IVRDisplayComponent | _003 | 実装(仮想ディスプレイ) |
| IVRServerDriverHost | _006 | 呼び出し(デバイス登録・姿勢送信) |
| IVRProperties | _001 | 呼び出し(WritePropertyBatch) |
| IVRDriverInput | _004 | 呼び出し(ボタン/スカラー/ハプティック) |
| IVRDriverLog | _001 | 呼び出し(ログ) |

## セクション

- [完了] 環境確認(SteamVR / .NET 10 / MSVC BuildTools / ヘッダ取得)
- [完了] ドライバ本体実装(vtable 相互運用、HMD、コントローラ、シミュレーション)
- [完了] ドライバパッケージ(manifest / vrsettings)と install.ps1 / uninstall.ps1
- [完了] NativeAOT ビルドと vrpathreg 登録
- [完了] SteamVR 起動でのスモークテスト(vrserver.txt でロード確認)
- [完了] フォーカス奪取対策: standby 無効化(IVRSettings で power.turnOffScreensTimeout=86400)、
  装着センサー常時 ON(Prop_ContainsProximitySensor_Bool + /proximity)で
  UserInteraction 遷移イベントを抑止。合成 W+D 入力で遷移ゼロを検証済み。
  ヘッドセットウィンドウの画面外配置は赤画面(present 全 drop)になるため不可と判明。
