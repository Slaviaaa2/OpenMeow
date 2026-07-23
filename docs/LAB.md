# OpenMeow.Lab

`OpenMeow.Lab` は、OpenMeow の操作ロジックを人が毎回 SteamVR 内で試す前に、
複数の AI エージェントが同じ条件で接触動作を研究するための軽量サンドボックスです。
実際の SteamVR ドライバとはプロセスを分離しているため、失敗した実験が
`vrserver.exe` を巻き込みません。

## できること

- 頭撫で、頬への接触、柔らかい腕の支持、手つなぎの4研究ステーション
- 手の追従ばね、減衰、速度・加速度上限、接触コンプライアンス、先読みの調整
- 接触率、目標部位だけの力と速度、反転回数、ジャーク、めり込み、離脱後の残留運動を同条件で採点
- エージェントごとに隔離した実験、リビジョンによる同時更新の衝突防止
- 複数候補の並列自動チューニングとランキング
- localhost JSON API と stdio MCP
- JSON ファイルによる研究対象モデルの追加

これは触覚や人体の真実を保証する物理シミュレータではありません。数値採点で候補を
絞り込んだ後、上位候補だけを実際の SteamVR で人が確認するための研究装置です。

評価器v3では、ストローク速度を手の全3次元速度ではなく研究項目の
`strokeAxis` 方向だけで測ります。これにより接近時の押し込み速度と、撫でる速さを
混同しません。また一度も目標へ触れていない試行には、速度・反転・静止の加点を
与えません。自動探索の標準動作も固定0.34秒ストロークではなく、移動距離を各項目の
`preferredStrokeSpeed` で割った時間を使うため、速度違反の軌道を全候補へ強制しません。

## 起動

```powershell
dotnet run --project .\src\OpenMeow.Lab\OpenMeow.Lab.csproj
```

既定では UI と `http://127.0.0.1:17777/` の API が起動します。

```powershell
# UIなしのAPI管制塔
dotnet run --project .\src\OpenMeow.Lab\OpenMeow.Lab.csproj -- --headless --port 17777

# Codex等から接続するstdio MCPサーバー
dotnet run --project .\src\OpenMeow.Lab\OpenMeow.Lab.csproj -- --mcp

# 決定論・競合防止・採点の組み込み検証
dotnet run --project .\src\OpenMeow.Lab\OpenMeow.Lab.csproj -- --self-test
```

MCP クライアントの設定例:

```json
{
  "mcpServers": {
    "openmeow-lab": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "D:\\RiderWorks\\OpenMeow\\src\\OpenMeow.Lab\\OpenMeow.Lab.csproj",
        "--",
        "--mcp"
      ]
    }
  }
}
```

## エージェント研究ループ

1. `list_tasks` と `list_subjects` で条件を確認する。
2. エージェントごとに `create_experiment` を呼ぶ。
3. `observe` で対象部位と手のワールド座標を取得する。
4. `act` または `run_sequence` で接近、接触、ストローク、支持、離脱を実行する。
   `durationSeconds` は目標へ瞬間移動して待つ時間ではなく、現在位置から目標までを
   smoothstep補間する軌道時間として扱われる。長くすると実際にゆっくり動く。
   最後の離脱アクションだけ `measureSettling: true` にすると、その離脱が接触を
   解いた時点から0.9秒の残留運動を測る。途中の短い接触切れは静止評価に混ぜない。
5. `evaluate` で成分別スコアと改善案を得る。
6. `compare` でエージェント間の候補を比較する。
7. `auto_tune` で有望な連続値パラメータを並列探索する。
8. `recommend_bindings` で利き手やマウスボタン中心の配置案を作る。
9. 終了した実験は `delete_experiment` で解放する。

更新系ツールには直前の `revision` を `expectedRevision` として渡せます。別エージェントが
先に更新した場合は stale revision エラーになるため、互いの操作を静かに上書きしません。

## モデルの差し替え

実行ファイル横の `models` ディレクトリに `*.json` を置いて再起動します。
例は `src/OpenMeow.Lab/models/example-soft-avatar.json` にあります。各部位は球として
定義し、`position`、`radius`、`mobility`、`softness` と任意の `parent` を持ちます。
研究タスクが参照する `head`、`left_cheek`、`left_forearm`、`right_hand` を含めると
すべての既定ステーションで利用できます。

## HTTP API

主な経路は次の通りです。

- `GET /health`, `/subjects`, `/tasks`, `/experiments`
- `POST /experiments`
- `GET /experiments/{id}`
- `POST /experiments/{id}/act`, `/sequence`, `/evaluate`, `/reset`, `/profile`
- `POST /compare`, `/autotune`, `/bindings`
- `POST /driver-profile/preview`, `/driver-profile/apply`
- `DELETE /experiments/{id}`

### Driver profile bridge

`preview_driver_profile` (MCP) and `POST /driver-profile/preview` project a
research `MotionProfile` onto the desktop driver's second-order hand solver.
Only `positionSpringHz`, `dampingRatio`, `maxSpeed`, `maxAcceleration`, and
`predictionSeconds` are shared; contact compliance, hand radius, and bindings
remain Lab-only. A `basePreset` (`Legacy`, `Comfort`, `Precise`, `Natural`, or
`Responsive`) supplies all other desktop settings and defaults to `Natural`.
The damping value is raised to the driver's safe minimum of `1.0`.

Preview is read-only and can be called freely by agents. `apply_driver_profile`
and `POST /driver-profile/apply` are explicit write operations: they save the
mapped settings to `%LOCALAPPDATA%\\OpenMeow\\motion-profile.cfg`. Research,
auto-tuning, and preview never call apply implicitly.

API は意図的に loopback のみにバインドします。LAN やインターネットへ公開する場合は、
認証、TLS、レート制限を持つ別のゲートウェイを前段に置いてください。

## Full-body gait research

`GaitProfile` と `GaitSimulator` は手の接触研究 (`ResearchWorld`) から独立した、
決定論的な 90 Hz の全身歩行サブシステムです。プロファイルは次の9項目です。

`bodyHeightMeters`, `hipFollowTau`, `hipLeanDegrees`, `footSpacingMeters`,
`strideLengthMeters`, `stepHeightMeters`, `gaitSmoothingTau`, `turnToeDegrees`,
`footPlantStrength`。Natural の既定値は順に `1.65, .08, 11, .20, .45, .06, .24, 7, .92`。
`Sanitize()` は NaN/Infinity を安全な既定値へ戻し、ランタイムと同じ範囲
（身長1.2–2.2、hip tau .03–1、lean 0–25、spacing .10–.45、stride .10–1.2、
step 0–.30、gait tau .02–1、toe 0–35、plant 0–1）へクランプします。

既定ベンチマークは `idle 1s → forward 2s → strafe 1.5s → turn-in-place 1.5s →
diagonal 1.5s → stop 1.5s`（計810サンプル）です。速度・旋回を平滑化し、ヒップの
追従遅れとリーン、左右交互の smoothstep 歩容、正弦波の足上げ、接地足のワールド
ロック（`footPlantStrength` ブレンド）、旋回時のつま先ヨー、停止時の収束を測ります。
足配置はDriverとLabで別実装せず、どちらも `OpenMeow.BodyGaitKernel` を実行します。
Labはコマンドから決定論的なHMD軌跡を作るだけなので、評価対象はDriverが出力する腰・両足姿勢と同じです。
床は y=0 の平面であり、これはゲーム物理や人体の真実を再現するものではありません。

HTTP の追加経路:

- `POST /gait/benchmark` — `GaitBenchmarkRequest` を受け、全サンプル、メトリクス、
  成分別評価、プロファイルを返します。`scenario` を省略すると上記の固定シナリオです。
- `POST /gait/autotune` — `GaitAutotuneRequest` (`candidates` は2–128、
  `parallelism` は1–16)を受け、`bestProfile`, `bestScore`, `topResults`を返します。
  実験を永続化せず、キャンセル可能な並列評価を行います。
- `POST /gait/driver-profile/preview` — gait の9項目を `basePreset`（既定 Natural）へ
  完全に写像します。書き込みはせず、現在の `enableBodyTrackers` トポロジー flag を保持します。
- `POST /gait/driver-profile/apply` — preview と同じ写像を保存します。`enableBodyTrackers`
  の真偽値を必ず明示し、明示的な適用結果（`applied`, `enableBodyTrackers`,
  `restartRequired`, `configPath`）を返します。研究ベンチマークや autotune は apply を呼びません。

同じ機能は MCP の `run_gait_benchmark`, `auto_tune_gait`,
`preview_gait_driver_profile`, `apply_gait_driver_profile` で利用できます。コマンドは
`idle`, `forward`, `strafe`, `turnInPlace`, `diagonal`, `stop` の6種類です。
評価成分（`evaluator_version` を除く）は0..1、総合点は0..100です。足のワールド滑り、接地高さ誤差、スイング
クリアランス、腰のピーク加速度/ジャーク、左右位相差、停止時の速度/オーバーシュート、
旋回つま先整列、有限性、移動量、ステップ数を採点し、移動またはステップが無い試行は
高得点にならないゲートを持ちます。これは候補を比較するための研究用ヒューリスティック
であり、実機での足裏接触、斜面、段差、外乱、アニメーション品質は未検証です。
v8 evaluator は、左右それぞれの完了 swing 時間と step 数の差から実測 phase asymmetry を計算し、移動中の実時間（`movementSeconds`）から cadence を計算し、理想
`1.8 steps/s`（sigma `.9`）への滑らかな近さを `cadence_naturalness` として加えます。
身長比の stride/step/spacing と lean/toe の形状事前分布を `anthropometric_shape`、
plant `.90`（sigma `.07`）への近さを `plant_compliance` として採点します。停止速度は
意図的な減速後のtail、接地滑りは接触深度で重み付けし、純旋回も移動coverageとして扱います。
v8 の重みは
slip 8、height 5、clearance 9、smooth 12、phase 8、settling 12、toe 8、movement 8、
stepping 5、cadence 10、anthropometric 10、plant compliance 5（計100）です。
`evaluator_version` は成分の範囲クランプ対象外で値8を返し、他の成分だけ0..1へクランプします。
移動量とステップ数のゲートは引き続き有効で、短すぎる stride や完全固定 plant の報酬搾取を抑えます。
シナリオは最大128 segment、sanitize 後の合計時間は120秒までで、ベンチマークと autotune は
キャンセルトークンを各segment/tickへ伝播します。各segmentの任意
`speedMultiplier` は `.1..3` へ安全化され、NaturalのSlow `.25` / Normal `1` /
Fast `2.25` を同じDriver入力軌跡で比較できます。
