# A running EgumaruMusic watcher owns the queue on this Windows boot.
# Its scheduler can start a GPU process at any time, even when memory is free.
$ErrorActionPreference = 'Stop'
try {
    $processes = Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'pythonw.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe'"
    $other = $processes | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -and
        ($_.CommandLine -match '(?i)(?:[\\/]EgumaruMusic[\\/]|[\\/]music\.ps1(?:["\s]|$))') -and
        ($_.CommandLine -notmatch '(?i)[\\/]TheoriaCast\.Music[\\/]worker[\\/](worker|render)\.py')
    }
    if ($other) { exit 1 }
    exit 0
} catch { exit 2 }
