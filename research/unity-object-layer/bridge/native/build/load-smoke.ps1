$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeSmoke {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryW(string path);
  [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeLibrary(IntPtr module);
}
'@
if ([IntPtr]::Size -ne 4) { throw 'Must be run in independent 32-bit Windows PowerShell' }
$path = Join-Path $PSScriptRoot 'bridge.dll'
$module = [NativeSmoke]::LoadLibraryW($path)
if ($module -eq [IntPtr]::Zero) { throw ('Own DLL load failed: ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
foreach ($name in @('JSB_Arm','JSB_Fire','JSB_Cancel','JSB_ReadStatus','JSB_Close')) {
  if ([NativeSmoke]::GetProcAddress($module,$name) -eq [IntPtr]::Zero) { throw "Missing export $name" }
}
# No hook was armed and no callback was registered or invoked, so local unload is safe.
if (-not [NativeSmoke]::FreeLibrary($module)) { throw 'Own unarmed DLL unload failed' }
$result = [ordered]@{status='PASS'; pointer_size=[IntPtr]::Size; own_dll_load=$true; unarmed_local_unload=$true; game_access=$false; callback_invoked=$false}
$result | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'load-smoke.json') -Encoding utf8
$result | ConvertTo-Json -Compress
