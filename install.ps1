# OpenMeow ドライバのビルド → dist 組み立て → SteamVR への登録
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Test-CommandExists([string]$name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Update-SessionPath {
    # インストーラー完了直後は現プロセスの PATH が更新されないため、レジストリから読み直す
    $machine = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [System.Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machine;$user"
}

function Request-InstallerRun([string]$label, [string]$url, [string]$fileName, [string[]]$arguments = @()) {
    $answer = Read-Host "$label が見つかりません。インストーラーをダウンロードして起動しますか? (y/n)"
    if ($answer -notmatch '^[Yy]') {
        throw "$label のインストールが必要です。インストール後に再実行してください。"
    }
    $installerPath = Join-Path $env:TEMP $fileName
    Write-Host "ダウンロード中: $url" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $installerPath -UseBasicParsing
    $signature = Get-AuthenticodeSignature -FilePath $installerPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
        throw "$label の署名検証に失敗しました。実行を中止します。"
    }
    Write-Host "インストーラーを起動します。インストールが完了したらこのウィンドウに戻ってください。" -ForegroundColor Cyan
    if ($arguments.Count -gt 0) {
        Start-Process -FilePath $installerPath -ArgumentList $arguments -Wait
    } else {
        Start-Process -FilePath $installerPath -Wait
    }
    Update-SessionPath
}

Write-Host "== 前提ツールの確認 ==" -ForegroundColor Cyan
if (-not (Test-CommandExists "dotnet")) {
    Request-InstallerRun ".NET SDK" "https://aka.ms/dotnet/10.0/dotnet-sdk-win-x64.exe" "dotnet-sdk-win-x64.exe" @("/passive", "/norestart")
    if (-not (Test-CommandExists "dotnet")) {
        throw ".NET SDK のインストールを確認できませんでした。インストール後に新しいターミナルを開いて再実行してください。"
    }
}

# 環境によっては NativeAOT の findvcvarsall.bat が stderr ノイズでリンカパスを壊すため、
# vswhere で見つけた VS 開発者環境 + IlcUseEnvironmentalTools でビルドする
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
function Get-VsDevCmdPath {
    if (-not (Test-Path $vswhere)) { return $null }
    $vsBase = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1
    if ($vsBase -and (Test-Path "$vsBase\Common7\Tools\VsDevCmd.bat")) { return "$vsBase\Common7\Tools\VsDevCmd.bat" }
    return $null
}
$vsDevCmd = Get-VsDevCmdPath
if (-not $vsDevCmd) {
    Request-InstallerRun "C++ Build Tools" "https://aka.ms/vs/17/release/vs_buildtools.exe" "vs_buildtools.exe" @("--add", "Microsoft.VisualStudio.Workload.VCTools", "--includeRecommended", "--passive", "--norestart")
    $vsDevCmd = Get-VsDevCmdPath
    if (-not $vsDevCmd) { Write-Host "C++ Build Tools が確認できませんでした。VsDevCmd なしでビルドを試みます。" -ForegroundColor Yellow }
}

Write-Host "== ビルド (NativeAOT) ==" -ForegroundColor Cyan
if ($vsDevCmd) {
    cmd /c "`"$vsDevCmd`" -arch=x64 -no_logo && dotnet publish `"$root\src\OpenMeow.Driver\OpenMeow.Driver.csproj`" -c Release -r win-x64 /p:IlcUseEnvironmentalTools=true"
} else {
    dotnet publish "$root\src\OpenMeow.Driver\OpenMeow.Driver.csproj" -c Release -r win-x64
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishDll = "$root\src\OpenMeow.Driver\bin\Release\net10.0\win-x64\publish\driver_openmeow.dll"
if (-not (Test-Path $publishDll)) { throw "publish output not found: $publishDll" }

Write-Host "== ビルド (オーバーレイ) ==" -ForegroundColor Cyan
dotnet publish "$root\src\OpenMeow.Overlay\OpenMeow.Overlay.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "overlay publish failed" }
$overlayPublish = "$root\src\OpenMeow.Overlay\bin\Release\net10.0-windows\publish"
if (-not (Test-Path "$overlayPublish\OpenMeowOverlay.exe")) { throw "overlay output not found" }

Write-Host "== dist\openmeow を組み立て ==" -ForegroundColor Cyan
$dist = "$root\dist\openmeow"
New-Item -ItemType Directory -Force "$dist\bin\win64" | Out-Null
Copy-Item -Recurse -Force "$root\driver\*" $dist

# steam.exe が presence チェックで DLL をロードしたままにする。ロック中でもリネームは可能なので
# 旧 DLL をタイムスタンプ付き .old に退避してから新しい DLL を置く(次回の SteamVR 起動で新版が使われる)。
$targetDll = "$dist\bin\win64\driver_openmeow.dll"
Get-ChildItem "$dist\bin\win64" -Filter "driver_openmeow.dll.old*" -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch {} }
if (Test-Path $targetDll) {
    try { Remove-Item $targetDll -Force -ErrorAction Stop }
    catch { Rename-Item $targetDll "driver_openmeow.dll.old-$(Get-Date -Format yyyyMMddHHmmss)" -Force }
}
Copy-Item -Force $publishDll $targetDll
$overlayExe = [System.IO.Path]::GetFullPath((Join-Path $dist "bin\win64\OpenMeowOverlay.exe"))
Get-Process -Name OpenMeowOverlay -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        if ([System.IO.Path]::GetFullPath($_.MainModule.FileName) -ieq $overlayExe) {
            Stop-Process -Id $_.Id -Force
        }
    } catch { }
}
Copy-Item -Force "$overlayPublish\*" "$dist\bin\win64\"

Write-Host "== ルームセットアップ免除 (chaperone データ直書き) ==" -ForegroundColor Cyan
# ドライバは Prop_CurrentUniverseId_Uint64=2 を名乗る。universe 2 のキャリブレーション済み
# データを置いておけば、ステータス C201(未キャリブレーション)とルームセットアップの
# 自動起動は発生しない。3m×3m の立位プレイエリア。
$steamRoot = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
if (-not $steamRoot) { $steamRoot = "C:\Program Files (x86)\Steam" }
$steamRoot = $steamRoot -replace "/", "\"
$chapPath = "$steamRoot\config\chaperone_info.vrchap"
# ネストの深い配列は PowerShell オブジェクト経由だと壊れやすいので生 JSON で持つ
$universeJson = @'
{
  "collision_bounds": [
    [ [-1.5,0,-1.5], [-1.5,2.43,-1.5], [-1.5,2.43,1.5], [-1.5,0,1.5] ],
    [ [-1.5,0,1.5],  [-1.5,2.43,1.5],  [1.5,2.43,1.5],  [1.5,0,1.5] ],
    [ [1.5,0,1.5],   [1.5,2.43,1.5],   [1.5,2.43,-1.5], [1.5,0,-1.5] ],
    [ [1.5,0,-1.5],  [1.5,2.43,-1.5],  [-1.5,2.43,-1.5],[-1.5,0,-1.5] ]
  ],
  "play_area": [ 3.0, 3.0 ],
  "seated":   { "translation": [ 0, 1.2, 0 ], "yaw": 0 },
  "standing": { "translation": [ 0, 0, 0 ],   "yaw": 0 },
  "time": "__TIME__",
  "universeID": "2"
}
'@ -replace "__TIME__", (Get-Date).ToString("ddd MMM dd HH:mm:ss yyyy")
if (Test-Path $chapPath) {
    $chap = Get-Content $chapPath -Raw | ConvertFrom-Json
    if ($chap.universes | Where-Object { $_.universeID -eq "2" }) {
        Write-Host "universe 2 は既に登録済み。chaperone はそのまま。"
    } else {
        $chap.universes = @($chap.universes) + @($universeJson | ConvertFrom-Json)
        $chap | ConvertTo-Json -Depth 12 | Set-Content $chapPath -Encoding UTF8
        Write-Host "既存 chaperone に universe 2 を追記した。"
    }
} else {
    "{`n  `"jsonid`": `"chaperone_info`",`n  `"universes`": [`n$universeJson`n  ],`n  `"version`": 5`n}" |
        Set-Content $chapPath -Encoding UTF8
    Write-Host "chaperone_info.vrchap を新規作成した。"
}

Write-Host "== SteamVR へ登録 ==" -ForegroundColor Cyan
# SteamVR は別ライブラリに入っていることもあるので libraryfolders.vdf から探す
$libraries = @($steamRoot)
$vdf = "$steamRoot\steamapps\libraryfolders.vdf"
if (Test-Path $vdf) {
    $libraries += (Get-Content $vdf | Select-String '"path"\s+"([^"]+)"').Matches |
        ForEach-Object { $_.Groups[1].Value -replace "\\\\", "\" }
}
$vrpathreg = $libraries |
    ForEach-Object { "$_\steamapps\common\SteamVR\bin\win64\vrpathreg.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vrpathreg) { throw "vrpathreg.exe が見つかりません。SteamVR はインストール済みですか?" }
& $vrpathreg adddriver $dist
& $vrpathreg show

Write-Host ""
Write-Host "完了。SteamVR を起動すると仮想 HMD + コントローラ2本が現れます。" -ForegroundColor Green
Write-Host "ログ: $env:LOCALAPPDATA\OpenMeow\openmeow_driver.log および SteamVR の vrserver.txt"
