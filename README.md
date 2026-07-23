# OpenMeow 🐱

![License: WTFPL](https://img.shields.io/badge/License-WTFPL-brightgreen.svg)
![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)

**VR 機材なしで SteamVR ゲームを遊ぶための仮想デバイスドライバ。**
仮想 HMD と Vive コントローラ 2 本を、キーボード+マウスの FPS ライクな操作で動かせます。
ドライバ本体は C++ を使わず .NET 10(NativeAOT)だけで実装されています。

> OpenMeow is a SteamVR virtual device driver written entirely in .NET 10 (NativeAOT).
> It emulates an HMD and two Vive wands so you can play VR games with a keyboard and
> mouse — no headset required.

## 特徴

- **ハードウェア不要** — 仮想 HMD + コントローラ 2 本が SteamVR に本物として認識される
- **低遅延ライブビュー** — コンポジタが HMD へ送る合成済みフレームをドライバが直接受け取り、
  コントロールパネルに表示(SteamVR の VR ビューを経由しない)
- **FPS ライクな操作** — マウスで見回し、クリックでトリガー。手の向きは注視点へ自動収束するので
  「見てクリック」だけで VR 内の UI に触れる
- **既定バインディングがそのまま動く** — コントローラは HTC Vive ワンドとして認識される
- **セットアップ最小** — ルームセットアップ不要。`install.ps1` 一発で導入完了

## 動作環境

| 要件 | 用途 |
|---|---|
| Windows 10/11 (x64) | 実行環境 |
| [SteamVR](https://store.steampowered.com/app/250820/SteamVR/) | 実行環境 |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | ビルド |
| Visual Studio 2022 / Build Tools(C++ ワークロード) | NativeAOT のリンク |

## インストール

### いちばん簡単な方法(PowerShell)

Gitや開発環境をあらかじめ用意する必要はありません。PowerShellを開き、次の1行を貼り付けて実行してください。

```powershell
irm https://raw.githubusercontent.com/Slaviaaa2/OpenMeow/main/bootstrap.ps1 | iex
```

このセットアップは、GitHubからOpenMeowのソース一式をHTTPSでダウンロードし、
`%LOCALAPPDATA%\OpenMeow\source` に保存してビルド・登録まで行います。`source` は
オンラインセットアップ専用の管理キャッシュで、実行のたびに最新版へ丸ごと置き換わります。
手動で変更する作業用コピーには使わないでください。新版のダウンロードと構成確認が完了するまでは
既存の `source` を変更せず、旧版は新版のビルドが成功するまで一時バックアップされます。
ビルドに失敗した場合は、調査と復旧のため新版と旧版の両方を残します。
途中で.NET SDKやC++ Build Toolsが必要になった場合は、公式インストーラーの起動を確認します。
この1行は、取得したセットアップスクリプトをそのまま実行する便利な方法です。
内容を確認してから実行したい場合は、URLの `bootstrap.ps1` を保存してからPowerShellで開いてください。
なお、上記URLは`main`ブランチの最新版を取得するため、再現性を重視する場合はタグやリリース版をご利用ください。

インストール中は案内と注意事項が表示されます。完了後にEnterキーを押すとウィンドウを閉じられます。

### ソースを既に取得している場合

```powershell
git clone https://github.com/Slaviaaa2/OpenMeow.git
cd OpenMeow
.\install.ps1     # ビルド → ドライバ組み立て → SteamVR への登録
```

インストールするとスタートメニューに **OpenMeow** が登録されます。これを開き、
コントロールパネル右上の **「SteamVR を起動」** ボタンを押すと、そのまま SteamVR が
立ち上がって仮想デバイスが認識されます(SteamVR を先に起動した場合も、コントロール
パネルは自動で開きます)。

アンインストール:

```powershell
.\uninstall.ps1
```

オンラインセットアップを利用した場合は、`%LOCALAPPDATA%\OpenMeow\source\uninstall.ps1` からも実行できます。
コントロールパネルの **設定 → ドライバ登録を削除** から開始することもできます。

## 使い方

コントロールパネルに VR 映像が表示されます。**映像をクリックすると操作開始**、
**ESC で解除**(チャット入力などに戻れます)。

操作はモードレスのホールド式です。ボタンを押している間だけ意味が変わり、
手は「置いた位置+頭への追従」を維持します(勝手に動きません)。

### 設定

コントロールパネル右上の **設定** から、タブごとにキー割り当てを変更できます。
割り当てボタンを押してから新しいキーを押し、保存してください。設定は
`%LOCALAPPDATA%\OpenMeow\keybindings.cfg` に保存され、ドライバへ自動反映されます。
同じ画面から初期設定への復元と、確認付きのドライバ登録削除も行えます。

### 外部ソフトへの映像出力

コントロールパネル右上の **映像出力** を押すと、操作UIを含まない左目映像専用の
`OpenMeow 映像出力` ウィンドウが開きます。メイン画面を最小化しても映像出力は
継続します。OBSでは **ウィンドウキャプチャ** を追加してこのウィンドウを選び、
**クライアント領域** を有効にしてください。出力ウィンドウは自由にリサイズできますが、
映像のアスペクト比は維持されます。

この機能は映像のみを出力します。ゲーム音声やマイク音声はOBS側で別途取り込んでください。

### マウス

| 入力 | 動作 |
|---|---|
| マウス移動 | 見回し(手は注視点に自動照準) |
| 左クリック / 右クリック | トリガー / グリップ(通常は右手、左手ホールド中は左手) |
| 左Shift +マウス | 右腕の位置(ホイール = 奥行き) |
| 左Shift +中ドラッグ | 右腕を肩から傾ける(中ドラッグ単独でも可) |
| 左Ctrl +マウス | 左腕の位置(ホイール = 奥行き) |
| 左Ctrl +中ドラッグ | 左腕を肩から傾ける |
| 腕傾け中に横 / 縦ドラッグ | 腕全体を横倒し / 上下に振る(斜め入力も可) |
| 腕傾け中にホイール | 手先の向きを左右へ振る |
| サイド奥 X1 / 手前 X2 +マウス | 左腕の位置 / 傾きへの直接ショートカット |
| Tab +マウス | 左トラックパッド(歩行)。離すと中央へ戻る |
| R +マウス | 右トラックパッド(旋回) |
| Tab / R +左クリック | そのパッドの押し込み |

### キーボード

| キー | 動作 |
|---|---|
| W / A / S / D | 移動(頭。手は追従) |
| Q / E | 下降 / 上昇 |
| ← → ↑ ↓ | 頭の微回転 |
| 右Shift / 右Ctrl | 高速 / 低速 |
| PgUp / PgDn | ホールド中の手の奥行き |
| Y / B | 右手 / 左手のグリップ保持トグル |
| F5 / F6 | マウス左右 / 上下の反転トグル |
| BackSpace | ホールド中の手(または頭)のリセット |
| Z X C V + T F G H + F7 | 左手のボタン類(トリガー/グリップ/メニュー/パッド/システム) |
| U O P M + I J K L + F8 | 右手のボタン類(同上) |

## トラブルシューティング

- ドライバログ: `%LOCALAPPDATA%\OpenMeow\openmeow_driver.log`
- SteamVR 側ログ: `<Steam>\logs\vrserver.txt`(`openmeow` で検索)
- ビルドは必ず `install.ps1` 経由で行ってください(NativeAOT のリンカ検出問題を回避します)
- ドライバ DLL が更新できない場合は Steam が旧 DLL を掴んでいます。
  `install.ps1` が自動で退避するのでそのまま再実行すれば問題ありません

## 仕組み

OpenVR ドライバ API は C++ の仮想クラス群ですが、本プロジェクトは vtable を
アンマネージドメモリに手動構築することで、C# (NativeAOT) のみで
`vrserver.exe` に直接ロードされるネイティブドライバ DLL を実現しています。
HMD は `IVRVirtualDisplay` の仮想ディスプレイとして動作し、コンポジタの合成済み
フレームを D3D11 経由で読み戻してコントロールパネルへストリーミングします。

詳細は [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) を参照してください。

## 腰・両足トラッカー（実験的）

設定の操作感タブで「腰・両足トラッカー」を有効にすると、SteamVR再起動後に
腰・左足・右足のGenericTrackerを追加します。既定はオフで、実行中の変更には再起動が必要です。
SteamVRの **Manage Trackers** で `OMEOW-TRK-WAIST` / `OMEOW-TRK-LEFT_FOOT` /
`OMEOW-TRK-RIGHT_FOOT` をそれぞれ Waist / Left Foot / Right Footへ手動割り当てしてください。
頭の水平移動・ヨーから自然な腰追従と交互歩行を合成する実験機能です。

## OpenMeow.Lab

`OpenMeow.Lab` は、複数のAIエージェントが頭撫で、頬への接触、手つなぎ、
柔らかい腕の支持を同じ条件で反復できる研究ハーネスです。決定論的な軟体モデル、
手接触評価器v3と、実Driverと同じ共有カーネルを測る全身歩行評価器v8、並列自動探索、
localhost API、stdio MCPを備え、研究プロファイルを
本体の操作感設定へプレビューまたは明示適用できます。

```powershell
dotnet run --project .\src\OpenMeow.Lab\OpenMeow.Lab.csproj -- --headless
```

詳しくは [Labガイド](docs/LAB.md) と
[デスクトップ操作感プロファイル](docs/MOTION_PROFILES.md) を参照してください。

## コントリビュート

Issue / Pull Request を歓迎します。

## ライセンス

[WTFPL](LICENSE)
