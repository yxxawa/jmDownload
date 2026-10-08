[CmdletBinding()]
param([switch]$ExpectMasked)
$ErrorActionPreference = 'Stop'
$taskWorkspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('jm-permission-' + [Guid]::NewGuid().ToString('N'))
$taskBlocked = Join-Path $taskRoot 'blocked'
foreach ($taskSubpath in @('blocked\Cache\content','blocked\Cache\reader','blocked\Fixture')) {
    [System.IO.Directory]::CreateDirectory((Join-Path $taskRoot $taskSubpath)) | Out-Null
}
$taskOriginalAcl = Get-Acl -LiteralPath $taskBlocked
$taskDeniedAcl = Get-Acl -LiteralPath $taskBlocked
$taskSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$taskRule = New-Object System.Security.AccessControl.FileSystemAccessRule($taskSid, [System.Security.AccessControl.FileSystemRights]::Write, [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Deny)
try {
    $taskDeniedAcl.AddAccessRule($taskRule)
    Set-Acl -LiteralPath $taskBlocked -AclObject $taskDeniedAcl
    $taskArguments = @('run','--project',(Join-Path $taskWorkspace 'DesktopShell.csproj'),'-c','Release','-p:SmokeTest=true','--','--storage-selfcheck',$taskRoot)
    if ($ExpectMasked) { $taskArguments += '--expect-masked' }
    & dotnet @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'Storage permission selfcheck failed' }
} finally {
    Set-Acl -LiteralPath $taskBlocked -AclObject $taskOriginalAcl
    Write-Host 'Isolated test-directory ACL restored; application and download directories were not changed.'
}
