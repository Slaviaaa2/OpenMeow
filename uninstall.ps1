# OpenMeow ドライバの登録解除
$ErrorActionPreference = "Stop"
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
& $vrpathreg removedriver "$PSScriptRoot\dist\openmeow"
& $vrpathreg show
Write-Host "登録を解除しました。dist フォルダは残っています(不要なら手動で削除してください)。"
