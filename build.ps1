param(
    [Parameter(Mandatory=$true)][string]$RuntimeDir,
    [string]$DotnetPath='dotnet'
)
$ErrorActionPreference='Stop'
$runtimePath=(Resolve-Path -LiteralPath $RuntimeDir).Path
$dependencyPath=Join-Path $PSScriptRoot 'dependencies'
New-Item -ItemType Directory -Path $dependencyPath -Force | Out-Null
foreach($name in @('RecompOne.Runtime.dll','recompone.dll')) {
    $file=Join-Path $runtimePath $name
    if(!(Test-Path -LiteralPath $file)){throw "Dependencia ausente: $file"}
    Copy-Item -LiteralPath $file -Destination $dependencyPath
}
# Visual/audio resources are separate from the code snapshot. Use the same
# installed release to retain the original HD pack and refined mesh.
$runtimeAssets=Join-Path $runtimePath 'assets'
if(Test-Path -LiteralPath $runtimeAssets) {
    Get-ChildItem -LiteralPath $runtimeAssets -Force | Copy-Item -Destination (Join-Path $PSScriptRoot 'dev/Tenchu-Windows/assets') -Recurse -Force
}
& $DotnetPath publish (Join-Path $PSScriptRoot 'dev/Tenchu-Windows/TenchuSARecomp.csproj') -c Release -r win-x64 --self-contained true -p:OutputType=WinExe -p:TenchuBenchmarks=true -o (Join-Path $PSScriptRoot 'dist/Tenchu-Shadow-Reborn') --nologo
if($LASTEXITCODE -ne 0){throw 'Falha ao compilar o jogo.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist/Tenchu-Shadow-Reborn/TenchuSARecomp.exe') -Destination (Join-Path $PSScriptRoot 'dist/Tenchu-Shadow-Reborn/Tenchu Shadow Reborn.exe')
Write-Output 'Compilacao concluida em dist/Tenchu-Shadow-Reborn.'
