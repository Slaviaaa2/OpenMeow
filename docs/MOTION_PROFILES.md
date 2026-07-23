# Desktop motion profiles

OpenMeow stores the selected desktop input profile in
`%LOCALAPPDATA%\OpenMeow\motion-profile.cfg`. The file is an invariant,
line-based `key=value` file and is replaced atomically. It is deliberately
NativeAOT-friendly (no JSON or reflection).

## Presets

| Preset | Intent | Hand smoothing |
| --- | --- | --- |
| `Legacy` | Existing OpenMeow behavior and defaults | First-order, tau 0.09 s |
| `Comfort` | Slower, softer motion for long sessions | Bounded second-order, 5.5 Hz, damping 1.35 |
| `Precise` | Small, deliberate hand and view changes | Bounded second-order, 6 Hz, damping 1.4 |
| `Natural` | Lab v3 cross-task maximin response | Bounded second-order, 6 Hz, damping 1.1 |
| `Responsive` | Faster response for active movement | Bounded second-order, 9 Hz, damping 1.05 |

The nonlegacy profiles use a short target-velocity prediction and clamp hand
velocity and acceleration. Damping is treated as critical or over-damped by the
runtime, and filter velocities are reset when a profile is reloaded or reset.
This is experimental tuning: comfort and tracking can vary by game, frame rate,
and user.

`Natural` は評価器v3の標準軌道で、頭撫で、頬、腕支持、手つなぎの4項目を
同じ候補で比較したmaximin（最低項目の点を最大化）です。採用値はばね6 Hz、
減衰1.1、速度上限1.5 m/s、加速度上限14 m/s²、先読み20 msです。

全身歩行は初期評価器で4,096候補、歩調と人体比を加えたv3で3,072候補、
左右対称性・有限性・接地柔軟性を修正したv4で2,048候補を探索しました。最終上位77候補は
停止多め・旋回多め・短い遷移・長距離の4条件へ再投入しています。
その後、研究器とDriverを同じ `BodyGaitKernel` へ統合したv8研究でさらに3,072候補を探索し、
停止多め・純旋回・短い遷移・長距離・Fast・Slowを含む7条件で上位を再評価しました。これにより倍速の歩調、
停止時の位相逆再生、弱い接地ロックも実ランタイム側で修正されています。
`Natural` の採用値は身長1.65 m、腰追従0.08 s、腰傾斜11°、足幅0.20 m、
歩幅0.45 m、足上げ0.06 m、歩行平滑化0.24 s、旋回つま先7°、接地0.92です。
身長と足幅はユーザー固有の校正値なので、自動探索中は固定しています。

Missing files, malformed values, or values outside the safe ranges load the
exact `Legacy` constants. The overlay's **操作感** tab provides a one-click
preset selector and highlights the key numeric values. Saving writes keybindings
and the motion profile together; the running driver checks both approximately
once per second, so a saved change can take up to about one second to apply.

The key IDs and their bindings are independent of motion profiles.

## 実験的な腰・両足トラッカー

操作感タブの「腰・両足トラッカー」は既定ではオフです。オンにして保存した後、
SteamVRを完全に終了して再起動すると、OpenMeowは腰・左足・右足の
GenericTrackerを追加します。設定のトポロジーはSteamVRセッション開始時に固定されるため、
実行中のチェック変更ではデバイスが増減しません。

SteamVRの **Manage Trackers** で、`OMEOW-TRK-WAIST` を Waist、
`OMEOW-TRK-LEFT_FOOT` を Left Foot、`OMEOW-TRK-RIGHT_FOOT` を Right Footへ
手動で割り当ててください。これは実験的なデスクトップ歩行合成で、頭の水平移動と
ヨーから腰追従・交互の足運び・旋回時のつま先向きを生成します。身長、歩幅、足上げ、
接地強度などの数値は操作感タブから調整できます。
