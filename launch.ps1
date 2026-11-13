$ErrorActionPreference = 'Stop'
try {
    $taskRoot = $PSScriptRoot
    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $pythonCommand) {
        throw 'Python 3.10+ was not found on PATH. Install Python with Tcl/Tk support first.'
    }
    $pythonRuntime = & $pythonCommand.Source -c 'import sys; print(sys.executable)'
    if ($LASTEXITCODE -ne 0 -or -not $pythonRuntime) {
        throw 'Could not resolve the Python runtime.'
    }
    $pythonWindowed = Join-Path (Split-Path -Parent $pythonRuntime.Trim()) 'pythonw.exe'
    if (-not (Test-Path -LiteralPath $pythonWindowed -PathType Leaf)) {
        throw "pythonw.exe was not found next to Python: $pythonRuntime"
    }
    $entryPoint = Join-Path $taskRoot 'main.pyw'
    Start-Process -FilePath $pythonWindowed -ArgumentList @(('"' + $entryPoint + '"')) -WorkingDirectory $taskRoot -WindowStyle Normal
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'Jiangshen Assistant - Startup Error') | Out-Null
    exit 1
}
