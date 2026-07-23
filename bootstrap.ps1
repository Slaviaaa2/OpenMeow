# OpenMeow オンラインセットアップ。Gitやcloneは必要ありません。
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$zipUrl = "https://github.com/Slaviaaa2/OpenMeow/archive/refs/heads/main.zip"
$installRoot = Join-Path $env:LOCALAPPDATA "OpenMeow\source"
$tempRoot = Join-Path $env:TEMP ("OpenMeow-download-" + [Guid]::NewGuid().ToString("N"))
$zipPath = Join-Path $tempRoot "OpenMeow.zip"

function Wait-ForUser {
    if ($Host.Name -notmatch "ServerRemoteHost") {
        [void](Read-Host "オンラインセットアップが終了しました。Enterキーを押すとこのウィンドウを閉じます")
    }
}

try {
    Write-Host @"
========================================
 OpenMeow オンラインセットアップ
========================================
Gitをインストールしたり、cloneしたりする必要はありません。
GitHubから最新版のソース一式をHTTPSでダウンロードし、
このPCの %LOCALAPPDATA%\OpenMeow\source に保存してセットアップします。

留意事項:
 - SteamVRは終了した状態で実行してください。
 - .NET SDK / C++ Build Toolsがなければ公式インストーラーを起動します。
 - SteamVRの設定を書き換え、ドライバ登録とスタートメニュー登録を行います。
 - 仮想HMDのため、実際の部屋の境界や衝突防止には使用できません。
"@ -ForegroundColor Cyan

    $answer = Read-Host "GitHubからダウンロードして続行しますか? (y/n)"
    if ($answer -notmatch '^[Yy]') { Write-Host "キャンセルしました。"; return }

    New-Item -ItemType Directory -Force $tempRoot | Out-Null
    Write-Host "GitHubからOpenMeowをダウンロードしています..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -LiteralPath $zipPath -DestinationPath $tempRoot -Force

    $extractedRoot = Join-Path $tempRoot "OpenMeow-main"
    if (-not (Test-Path (Join-Path $extractedRoot "install.ps1"))) {
        throw "ダウンロードしたOpenMeowの内容を確認できませんでした。"
    }
    New-Item -ItemType Directory -Force $installRoot | Out-Null
    Copy-Item -Path (Join-Path $extractedRoot "*") -Destination $installRoot -Recurse -Force

    $installer = Join-Path $installRoot "install.ps1"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -NoPause
    if ($LASTEXITCODE -ne 0) { throw "OpenMeowのセットアップが失敗しました。" }
    Write-Host "オンラインセットアップが完了しました。" -ForegroundColor Green
}
catch {
    Write-Host "オンラインセットアップに失敗しました: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (Test-Path $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Wait-ForUser
}
