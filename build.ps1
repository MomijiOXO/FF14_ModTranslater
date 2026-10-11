param([string]$SdkRoot = "$PSScriptRoot/work/dotnet", [string]$DalamudRoot = "$PSScriptRoot/work/dalamud-cn")
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $SdkRoot 'dotnet.exe'
$compiler = Get-ChildItem (Join-Path $SdkRoot 'sdk') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$refs = Get-ChildItem (Join-Path $SdkRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$compiler -or !$refs) { throw '需要 .NET 10 SDK，请使用 -SdkRoot 指定 SDK 根目录。' }
$framework = @(Get-ChildItem (Join-Path $refs.FullName 'ref/net10.0') -Filter '*.dll' | ForEach-Object { '/reference:"' + $_.FullName + '"' })
$out = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
$globalSource = Join-Path $out 'GlobalUsings.g.cs'
Set-Content -LiteralPath $globalSource -Encoding utf8 'global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks; global using System.Net.Http;'
function Compile-Project([string]$name, [string]$sourceDir, [string]$kind, [string[]]$additional) {
    $response = Join-Path $out "$name.rsp"
    $sources = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { '"' + $_.FullName + '"' })
    $argsList = @('/nologo','/langversion:latest','/nullable:enable','/deterministic','/debug:portable',"/target:$kind",('/out:"' + (Join-Path $out "$name.dll") + '"'),('"' + $globalSource + '"')) + $framework + $additional + $sources
    Set-Content -LiteralPath $response -Encoding utf8 $argsList
    & $dotnet (Join-Path $compiler.FullName 'Roslyn/bincore/csc.dll') "@$response"
    if ($LASTEXITCODE -ne 0) { throw "$name 编译失败" }
}
Compile-Project 'ModTranslater.Core' (Join-Path $PSScriptRoot 'src/ModTranslater.Core') 'library' @()
if (!(Test-Path (Join-Path $DalamudRoot 'Dalamud.dll'))) { throw '请使用 -DalamudRoot 指定卫月程序集目录。' }
$libs = @(Get-ChildItem -LiteralPath $DalamudRoot -Filter '*.dll' | Where-Object { $_.Name -notmatch '^(System\.|Microsoft\.|cimgui|cimguizmo|cimplot|lib|nethost|hostfxr|hostpolicy)' } | ForEach-Object {
    try { [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; '/reference:"' + $_.FullName + '"' } catch { }
})
Compile-Project 'ModTranslater' (Join-Path $PSScriptRoot 'src/ModTranslater') 'library' ($libs + @(('/reference:"' + (Join-Path $out 'ModTranslater.Core.dll') + '"')))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/ModTranslater/ModTranslater.json') -Destination $out
$manifestPath = Join-Path $out 'ModTranslater.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.DalamudApiLevel = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $DalamudRoot 'Dalamud.dll')).Version.Major
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "已生成 $out/ModTranslater.dll (Dalamud API $($manifest.DalamudApiLevel))"

