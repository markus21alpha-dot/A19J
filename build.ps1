param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
    [string]$EditorDataPath = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data'
)
$ErrorActionPreference='Stop'
$editor=$EditorDataPath
$managed=Join-Path $GamePath 'NuclearOption_Data\Managed'
$build=Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$argsList=@('/nostdlib+','/target:library','/langversion:latest','/optimize+','/deterministic+',('/out:"'+(Join-Path $build 'A19J.dll')+'"'))
foreach($name in @('mscorlib.dll','netstandard.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.ParticleSystemModule.dll','UnityEngine.AudioModule.dll','Mirage.dll','Mirage.SocketLayer.dll','Mirage.Sockets.Udp.dll','UniTask.dll','UnityEngine.AssetBundleModule.dll','UnityEngine.UnityWebRequestModule.dll')) {
    $argsList += '/reference:"'+(Join-Path $managed $name)+'"'
}
foreach($name in @('BepInEx.dll','0Harmony.dll')) { $argsList += '/reference:"'+(Join-Path $GamePath ('BepInEx\core\'+$name))+'"' }
$argsList += '/reference:"'+(Join-Path $GamePath 'BepInEx\plugins\com.nikkorap.blueprinter\Blueprinter_2.1.1.dll')+'"'
$argsList += '"'+(Join-Path $PSScriptRoot 'A19JPlugin.cs')+'"'
$argsList += '"'+(Join-Path $PSScriptRoot 'A19JNetworking.cs')+'"'
$argsList += '"'+(Join-Path $PSScriptRoot 'A19JMultiplayerValidation.cs')+'"'
$response=Join-Path $build 'compile.rsp'
$argsList | Set-Content -LiteralPath $response
& (Join-Path $editor 'NetCoreRuntime\dotnet.exe') (Join-Path $editor 'DotNetSdkRoslyn\csc.dll') ('@'+$response)
if($LASTEXITCODE -ne 0) {throw 'A19J compilation failed.'}
Get-FileHash -LiteralPath (Join-Path $build 'A19J.dll') -Algorithm SHA256


