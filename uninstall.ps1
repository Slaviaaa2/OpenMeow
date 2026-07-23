# OpenMeow ドライバの登録解除
[CmdletBinding()]
param([switch]$NoPause)

$ErrorActionPreference = "Stop"

function Wait-ForUser {
    if (-not $NoPause -and $Host.Name -notmatch "ServerRemoteHost") {
        [void](Read-Host "アンインストールが終了しました。Enterキーを押すとこのウィンドウを閉じます")
    }
}

trap {
    Write-Host ""
    Write-Host "アンインストールに失敗しました: $($_.Exception.Message)" -ForegroundColor Red
    Wait-ForUser
    exit 1
}

Write-Host @"
========================================
 OpenMeow アンインストール
========================================
SteamVRへのドライバ登録と、スタートメニューのOpenMeow登録を削除します。
distやソースファイル自体は安全のため残します。
SteamVRは終了した状態で実行してください。
"@ -ForegroundColor Cyan
$startAnswer = Read-Host "続行しますか? (y/n)"
if ($startAnswer -notmatch '^[Yy]') {
    Write-Host "キャンセルしました。"
    Wait-ForUser
    exit 0
}
$driverRoot = Join-Path $PSScriptRoot "dist\openmeow"
if (-not (Test-Path $driverRoot)) { $driverRoot = $PSScriptRoot }

# 設定画面から実行された場合も、同じdist内のオーバーレイだけを終了する。
$overlayExe = [System.IO.Path]::GetFullPath((Join-Path $driverRoot "bin\win64\OpenMeowOverlay.exe"))
Get-Process -Name OpenMeowOverlay -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        if ([System.IO.Path]::GetFullPath($_.MainModule.FileName) -ieq $overlayExe) {
            Stop-Process -Id $_.Id -Force
        }
    } catch { }
}
$steamRoot = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
if (-not $steamRoot) { $steamRoot = "C:\Program Files (x86)\Steam" }
$steamRoot = $steamRoot -replace "/", "\"
$libraries = @($steamRoot)
$vdf = "$steamRoot\steamapps\libraryfolders.vdf"
if (Test-Path $vdf) {
    $libraries += (Get-Content $vdf | Select-String '"path"\s+"([^"]+)"').Matches |
        ForEach-Object { $_.Groups[1].Value -replace "\\\\", "\" }
}
$vrpathreg = $libraries |
    ForEach-Object { "$_\steamapps\common\SteamVR\bin\win64\vrpathreg.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vrpathreg) { throw "vrpathreg.exe が見つかりません。" }
& $vrpathreg removedriver $driverRoot
& $vrpathreg show

# スタートメニューのショートカットを削除
$lnkPath = Join-Path ([Environment]::GetFolderPath("Programs")) "OpenMeow.lnk"
if (Test-Path $lnkPath) {
    Remove-Item -LiteralPath $lnkPath -Force -ErrorAction SilentlyContinue
    Write-Host "スタートメニューの『OpenMeow』を削除しました。"
}

Write-Host "登録を解除しました。dist フォルダは残っています(不要なら手動で削除してください)。"
Wait-ForUser
