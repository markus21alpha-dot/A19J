param([string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option')
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $PSScriptRoot 'build\A19J.dll'))
function Get-A19JAuditTypes($type) {
    $type
    foreach($nested in $type.NestedTypes) { Get-A19JAuditTypes $nested }
}
$types=@($assembly.MainModule.Types | ForEach-Object { Get-A19JAuditTypes $_ })
$callSites=@(foreach($type in $types) {
    foreach($method in $type.Methods) {
        if($method.HasBody) {
            foreach($instruction in $method.Body.Instructions) {
                if($instruction.Operand -is [Mono.Cecil.MethodReference]) { [pscustomobject]@{Caller=$method.FullName;Call=$instruction.Operand.FullName} }
            }
        }
    }
})
$calls=@($callSites.Call | Sort-Object -Unique)
$testOnlyNetwork=@($callSites | Where-Object {
    ($_.Caller -match 'A19JConversion.A19JPlugin::ValidationLoopbackBind\(') -and
    ($_.Call -in @('System.Void System.Net.IPEndPoint::.ctor(System.Net.IPAddress,System.Int32)',
                  'System.Void Mirage.Sockets.Udp.UdpConnectionHandle::.ctor(System.Net.EndPoint)'))
})
$forbidden=@($callSites | Where-Object {
    ($_.Call -match 'System\.(IO\.|Net\.|Diagnostics\.Process|Management\.)|Microsoft\.Win32\.Registry|DllImport|LoadLibrary|UnityEngine\.Networking\.|Application::OpenURL|Environment::get_(User|Machine)|Environment::GetFolderPath') -and
    ($_ -notin $testOnlyNetwork)
})
$audit=[ordered]@{
    Scope='Static direct-call and dependency review of the A19J plugin only; not a universal safety guarantee'
    References=@($assembly.MainModule.AssemblyReferences | ForEach-Object { $_.Name })
    DLLSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'build\A19J.dll') -Algorithm SHA256).Hash
    SourceSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'A19JPlugin.cs') -Algorithm SHA256).Hash
    SourceFiles=@('A19JPlugin.cs','A19JNetworking.cs','A19JMultiplayerValidation.cs' | ForEach-Object {[ordered]@{File=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash}})
    DirectMethodReferenceCount=$calls.Count
    ForbiddenCalls=$forbidden
    EnvironmentReads=@($calls | Where-Object { $_ -match 'Environment::' })
    ValidationSwitches=@('A19J_VALIDATE','A19J_VALIDATE_SPAWN','A19J_VALIDATE_MULTIPLAYER')
    TestOnlyNetworkCalls=$testOnlyNetwork
    TestNetworkScope='Only explicit A19J_VALIDATE=1 installs the loopback bind patch. Its endpoint is IPAddress.IPv6Loopback, the native transport is forced to managed UDP, and Steam transports are rejected in that disposable process. Ordinary gameplay uses native game networking.'
    PInvokeMethods=@($types | ForEach-Object { $_.Methods | Where-Object IsPInvokeImpl | ForEach-Object { $_.FullName } })
}
$audit | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'code-audit.json')
$calls | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'direct-call-references.txt')
if($forbidden.Count -gt 0 -or $audit.PInvokeMethods.Count -gt 0) { throw 'Direct-call audit needs manual review.' }
$audit | ConvertTo-Json -Depth 6
