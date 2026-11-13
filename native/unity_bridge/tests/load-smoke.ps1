$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AssistantNativeSmoke {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)] public static extern IntPtr LoadLibraryW(string path);
  [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
  [DllImport("kernel32.dll", ExactSpelling=true)] public static extern bool FreeLibrary(IntPtr module);
}
'@
if ([IntPtr]::Size -ne 4) { throw 'Requires independent 32-bit Windows PowerShell' }
$here = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent (Split-Path -Parent $here)
$dll = Join-Path $project 'jiangshen_assistant/runtime/win32/bridge.dll'
$module = [AssistantNativeSmoke]::LoadLibraryW($dll)
if ($module -eq [IntPtr]::Zero) { throw ('Own packaged DLL LoadLibrary failed: ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
foreach ($name in @('JSB_Arm','JSB_Fire','JSB_Cancel','JSB_ReadStatus','JSB_Close')) {
    if ([AssistantNativeSmoke]::GetProcAddress($module,$name) -eq [IntPtr]::Zero) { throw "Missing export $name" }
}
# No Arm or callback has occurred; local unload is safe and does not unload game code.
if (-not [AssistantNativeSmoke]::FreeLibrary($module)) { throw 'Own unarmed DLL unload failed' }
[ordered]@{status='PASS'; pointer_size=4; own_dll_load=$true; unarmed_local_unload=$true; game_access=$false; callback_invoked=$false} |
    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $here 'build/native-load-smoke.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $here 'build/native-load-smoke.json')
