param([string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option')
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $PSScriptRoot 'build\A19J.dll'))
function Get-A19JAuditTypes($type) {
    $type
    foreach($nested in $type.NestedTypes) { Get-A19JAuditTypes $nested }
}
$types=@($assembly.MainModule.Types | ForEach-Object { Get-A19JAuditTypes $_ })
$calls=@(foreach($type in $types) {
    foreach($method in $type.Methods) {
        if($method.HasBody) {
            foreach($instruction in $method.Body.Instructions) {
                if($instruction.Operand -is [Mono.Cecil.MethodReference]) { $instruction.Operand.FullName }
            }
        }
    }
}) | Sort-Object -Unique
$forbidden=@($calls | Where-Object {
    $_ -match 'System\.(IO\.|Net\.|Diagnostics\.Process|Management\.)|Microsoft\.Win32\.Registry|DllImport|LoadLibrary|UnityEngine\.Networking\.|Application::OpenURL|Environment::get_(User|Machine)|Environment::GetFolderPath'
})
$audit=[ordered]@{
    Scope='Static direct-call and dependency review of the A19J plugin only; not a universal safety guarantee'
    References=@($assembly.MainModule.AssemblyReferences | ForEach-Object { $_.Name })
    DLLSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'build\A19J.dll') -Algorithm SHA256).Hash
    SourceSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'A19JPlugin.cs') -Algorithm SHA256).Hash
    DirectMethodReferenceCount=$calls.Count
    ForbiddenCalls=$forbidden
    EnvironmentReads=@($calls | Where-Object { $_ -match 'Environment::' })
    ValidationSwitches=@('A19J_VALIDATE','A19J_VALIDATE_SPAWN')
    PInvokeMethods=@($types | ForEach-Object { $_.Methods | Where-Object IsPInvokeImpl | ForEach-Object { $_.FullName } })
}
$audit | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'code-audit.json')
$calls | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'direct-call-references.txt')
if($forbidden.Count -gt 0 -or $audit.PInvokeMethods.Count -gt 0) { throw 'Direct-call audit needs manual review.' }
$audit | ConvertTo-Json -Depth 6
