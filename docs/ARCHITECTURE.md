# アーキテクチャ

## 全体像

```
┌─────────────────────────── vrserver.exe ───────────────────────────┐
│  driver_openmeow.dll (.NET 10 NativeAOT)                           │
│  ├─ Provider      IServerTrackedDeviceProvider_004 / エントリポイント │
│  ├─ Devices       HMD + コントローラ×2 (ITrackedDeviceServerDriver)  │
│  │   ├─ IVRDisplayComponent_003   投影・解像度                      │
│  │   └─ IVRVirtualDisplay_002     擬似 vsync / フレーム受信          │
│  ├─ Simulation    入力 → 姿勢・ボタンの合成 (90Hz)                   │
│  ├─ FrameMirror   合成フレームを D3D11 で読み戻し → 共有メモリ        │
│  └─ ControlLink   コントロールパネルからの入力を共有メモリで受信       │
└────────────────────────────────────────────────────────────────────┘
        ↑ 入力 (Local\OpenMeowControl)     ↓ 映像 (Local\OpenMeowFrame)
┌────────────────────────────────────────────────────────────────────┐
│  OpenMeowOverlay.exe (WinForms コントロールパネル)                   │
│  マウスキャプチャ / ライブビュー描画 / 状態表示                        │
└────────────────────────────────────────────────────────────────────┘
```

## C# だけでネイティブドライバを作る方法

OpenVR ドライバ API は C++ 仮想クラスの集合であり、通常は C++ でしか実装できない。
本プロジェクトでは以下の手法で C#(NativeAOT)のみで実装している。

- **エクスポート**: `HmdDriverFactory` を `[UnmanagedCallersOnly(EntryPoint = ...)]` で
  ネイティブエクスポートする(csproj で `NativeLib=Shared`)。
- **呼ばれる側(ドライバが実装するインターフェース)**: vtable(関数ポインタ配列)を
  `NativeMemory.Alloc` で確保し、`delegate* unmanaged` で埋めた「オブジェクト」を返す。
- **呼ぶ側(vrserver が提供するインターフェース)**: 受け取ったオブジェクトポインタの
  vtable スロットを `delegate* unmanaged` として直接呼び出す。
- **ABI 注意点**(x64 MSVC メンバ関数):
  - `this` は第1引数。
  - 大きな構造体の値返し(`ComputeDistortion` 等)は `this` の次に隠し戻り値ポインタが入る。
  - C++ `bool` は 1 バイト(C# では `byte`)。
- 定数・構造体レイアウト・vtable 順序はすべて `openvr_driver.h` および
  Windows SDK ヘッダの現物から転記している(各ソースのコメントに出典を記載)。

## 表示系: IVRVirtualDisplay

HMD を「デスクトップ上のウィンドウ(拡張モード)」として実装すると、コンポジタが
フルスクリーン独占を要求してゲームとフォーカスを奪い合い、実用にならない
(SteamVR に「ヘッドセットエラー (-202)」「フルスクリーン喪失 (-203)」が出続ける)。

そのため無線 HMD 等が使う `IVRVirtualDisplay` を実装している:

- `Present` — コンポジタから合成済みバックバッファ(D3D11 共有テクスチャ)が届く
- `WaitForPresent` / `GetTimeSinceLastVsync` — 実ディスプレイがないため、
  Stopwatch ベースの 90Hz 擬似 vsync でフレームペーシングを再現する

これによりコンポジタはウィンドウを一切作らず、フォーカス問題が構造的に消える。

### フレームミラー(ライブビュー)

`Present` で届くテクスチャハンドルを `ID3D11Device::OpenSharedResource` で開き、
ステージングテクスチャへコピーして CPU に読み戻し、共有メモリ
(`Local\OpenMeowFrame`)へ書き込む。コントロールパネルが左目分を GDI で描画する。

重要: この共有テクスチャは **keyed mutex 付き**(`MiscFlags = SHARED_KEYEDMUTEX`)。
`IDXGIKeyedMutex::AcquireSync` を挟まずに読むと、エラーは出ないが**全ピクセルが
黒**になる。読み出しは Acquire/Release で囲むこと。

共有メモリはシーケンス番号の偶奇プロトコル(書き込み中は奇数)で、
読み手は読了後に番号が変わっていないことを確認してから採用する。

## 入力系

- コントロールパネルがマウスをキャプチャ(カーソル中央固定+移動量回収方式)し、
  累積値として共有メモリ(`Local\OpenMeowControl`)へ書く。読み手(ドライバ)が
  前回値との差分を取ることで、リセット競合なしに転送できる。
- キーボードとマウスボタンはドライバ側が `GetAsyncKeyState` でグローバルに読む。
  どのウィンドウにフォーカスがあっても効く。
- パネル未接続時は F9/F10 のグローバルキーによるフォールバック操作系に切り替わる。

### 姿勢の合成(Simulation)

- 手の向きは「視線上 2m の注視点」へ収束させる。手は頭からオフセットした位置に
  あるため、視線と平行に向けると近距離でレーザーが注視点から必ずずれる。
- 手の位置操作はビュー平面基準(頭のピッチを考慮)。見下ろし/見上げ時も
  マウスの方向と画面内の移動方向が一致する。
- すべてのポーズは指数バネ補間(時定数 ~0.1s)で目標へ追従し、瞬間移動しない。
- HMD には ±1mm の微小揺れを常時加える。完全静止だと SteamVR がデバイスを
  非アクティブ扱いし、復帰時のウィンドウ前面化がゲームのフォーカスを奪うため。

## SteamVR 統合の要点

| 対策 | 理由 |
|---|---|
| `Prop_ContainsProximitySensor_Bool` + `/proximity` 常時 true | HMD が Idle 化 → 復帰イベントでウィンドウが前面化するのを防ぐ |
| `power.turnOffScreensTimeout` を 24h に上書き | 5 秒放置でスタンバイ→復帰の繰り返しを防ぐ |
| `collisionBounds.CollisionBoundsStyle = NONE` | 仮想移動でシャペロンの壁が常時表示されるのを防ぐ |
| `chaperone_info.vrchap` に universe 2 の立位データを直書き | ルームセットアップの強制起動(C201)を回避 |
| コントローラを `vive_controller` として申告 | 各ゲームの既定バインディングを流用 |

## ビルドの注意

環境によっては NativeAOT の `findvcvarsall.bat` が vswhere の stderr 出力に汚染されて
リンカパスの解決に失敗する。`install.ps1` は vswhere で見つけた VS 開発者環境を
先に読み込み、`/p:IlcUseEnvironmentalTools=true` でリンクすることでこれを回避している。

また、SteamVR 終了後も Steam 本体がドライバ DLL をロードしたままにするため、
上書きできないことがある。`install.ps1` は旧 DLL をリネーム退避してから新 DLL を
配置する(ロック中でもリネームは可能)。
