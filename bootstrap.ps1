# OpenMeow オンラインセットアップ。Gitやcloneは必要ありません。
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$zipUrl = "https://github.com/Slaviaaa2/OpenMeow/archive/refs/heads/main.zip"
$openMeowRoot = Join-Path $env:LOCALAPPDATA "OpenMeow"
$installRoot = Join-Path $openMeowRoot "source"
$runId = (Get-Date -Format "yyyyMMddHHmmss") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$stagingRoot = Join-Path $openMeowRoot ("source.staging-" + $runId)
$backupRoot = Join-Path $openMeowRoot ("source.previous-" + $runId)
$tempRoot = Join-Path $env:TEMP ("OpenMeow-download-" + [Guid]::NewGuid().ToString("N"))
$zipPath = Join-Path $tempRoot "OpenMeow.zip"
$sourceSwapped = $false

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
このPCの %LOCALAPPDATA%\OpenMeow\source を最新版へ丸ごと更新して
セットアップします。このフォルダーはオンラインセットアップ専用です。

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
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -Headers @{ "Cache-Control" = "no-cache" }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $tempRoot -Force

    $extractedRoot = Join-Path $tempRoot "OpenMeow-main"
    if (-not (Test-Path (Join-Path $extractedRoot "install.ps1"))) {
        throw "ダウンロードしたOpenMeowの内容を確認できませんでした。"
    }

    # source は管理キャッシュとして毎回丸ごと入れ替える。既存フォルダーへの
    # 上書きコピーだと、古い bin/obj や削除済みファイルが残ってしまう。
    New-Item -ItemType Directory -Force $openMeowRoot | Out-Null
    New-Item -ItemType Directory $stagingRoot | Out-Null
    Copy-Item -Path (Join-Path $extractedRoot "*") -Destination $stagingRoot -Recurse -Force
    if (-not (Test-Path (Join-Path $stagingRoot "install.ps1")) -or
        -not (Test-Path (Join-Path $stagingRoot "src\OpenMeow.Overlay\OpenMeow.Overlay.csproj")) -or
        -not (Test-Path (Join-Path $stagingRoot "src\OpenMeow.Driver\OpenMeow.Driver.csproj"))) {
        throw "展開したOpenMeowのソース構成を確認できませんでした。"
    }

    if (Test-Path $installRoot) {
        Move-Item -LiteralPath $installRoot -Destination $backupRoot
    }
    try {
        Move-Item -LiteralPath $stagingRoot -Destination $installRoot
        $sourceSwapped = $true
    }
    catch {
        if ((Test-Path $backupRoot) -and -not (Test-Path $installRoot)) {
            Move-Item -LiteralPath $backupRoot -Destination $installRoot
        }
        throw
    }

    $installer = Join-Path $installRoot "install.ps1"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -NoPause
    if ($LASTEXITCODE -ne 0) { throw "OpenMeowのセットアップが失敗しました。" }
    if (Test-Path $backupRoot) {
        try { Remove-Item -LiteralPath $backupRoot -Recurse -Force }
        catch { Write-Host "以前のソースを削除できませんでした: $backupRoot" -ForegroundColor Yellow }
    }
    Write-Host "オンラインセットアップが完了しました。" -ForegroundColor Green
}
catch {
    Write-Host "オンラインセットアップに失敗しました: $($_.Exception.Message)" -ForegroundColor Red
    if ($sourceSwapped -and (Test-Path $backupRoot)) {
        Write-Host "最新版は source、以前のソースは $backupRoot に残しています。" -ForegroundColor Yellow
    }
}
finally {
    if (Test-Path $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Wait-ForUser
}
