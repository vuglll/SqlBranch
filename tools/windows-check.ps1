<#
.SYNOPSIS
    Checks SqlBranch on Docker Desktop for Windows: init, an image build, a branch create and a login.

.DESCRIPTION
    Run it from a checkout of the repository on a Windows machine that has Docker Desktop (Linux containers,
    running) and the .NET 10 SDK. It builds the command line, then runs these steps and writes everything
    it printed, with the exit code of each step, to a log file:

      1. init            (creates an engine, and a pool of its own)
      2. image build     (from a small backup made in the engine, or from -BakPath)
      3. branch create   (from that image)
      4. branch list and status
      5. branch conn, a TCP check of the engine port from Windows, then SELECT 1 through sqlcmd run in
         a separate container that connects to host.docker.internal
      6. branch rm and teardown --force

    It never uses the defaults of the tool. The engine container, pool volume, pool folder, port and password
    all have names of their own with a random id (sqlbranch-it-engine-<id>, sqlbranch-it-pool-<id>,
    /var/lib/sqlbranch/it-<id>), the same way the integration tests isolate themselves. The real defaults
    (container sqlbranch-engine, port 14330, /var/lib/sqlbranch/pool.xfs, volume sqlbranch-pool) are never
    touched, and the script removes only what it created. It makes no change outside Docker and its log file.

    The connection string and the password are never written to the log.

.PARAMETER BakPath
    A full backup (.bak) of a small database that SQL Server 2022 can restore. When it is left out, the script
    creates a database in the new engine, backs it up and copies the file out. A path to a file you have is
    only needed when that does not work.

.PARAMETER PoolBackend
    loop-xfs (default): the managed XFS pool, which is what Windows needs to prove. It fails with exit 3
    pool_unavailable when the pool cannot be mounted. auto: the same, but fall back to copy mode and say why.
    Run the script twice, once with each, if the first fails.

.PARAMETER PoolSize
    Size of the pool file. Default 2GB.

.PARAMETER LogPath
    Where to write the log. Default: windows-check-<id>.log in the current folder.

.EXAMPLE
    pwsh -File tools\windows-check.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\windows-check.ps1 -PoolBackend auto

.NOTES
    Exit code: 0 when every step gave the expected result, 1 when one did not, 2 when the script could not start
    (Docker or dotnet missing). Send back the whole log: see docs/research/windows-check.md.
#>
[CmdletBinding()]
param(
    [string]$BakPath,
    [ValidateSet('loop-xfs', 'auto')]
    [string]$PoolBackend = 'loop-xfs',
    [string]$PoolSize = '2GB',
    [string]$LogPath
)

$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
if (-not $LogPath) { $LogPath = Join-Path (Get-Location) "windows-check-$id.log" }
Set-Content -Path $LogPath -Value '' -Encoding UTF8

$engineName = "sqlbranch-it-engine-$id"
$volumeName = "sqlbranch-it-pool-$id"
$poolRoot = "/var/lib/sqlbranch/it-$id"
$homeDir = Join-Path ([IO.Path]::GetTempPath()) "sqlbranch-it-home-$id"   # never created
$buildDir = Join-Path ([IO.Path]::GetTempPath()) "sqlbranch-windows-check-$id"
$workDir = Join-Path $buildDir 'work'
$branch = 'win-check'
$image = 'shop'
$helperImage = 'ubuntu:24.04'   # the helper image the tool itself uses
$sqlcmdImage = 'mcr.microsoft.com/mssql/server:2022-latest'   # the default engine image; only its sqlcmd is used
$configPath = Join-Path ([IO.Path]::GetTempPath()) "sqlbranch-it-config-$id.yml"   # empty: no repository or user settings apply

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$password = 'Sb#' + [guid]::NewGuid().ToString('N')

$script:results = New-Object System.Collections.ArrayList

function Write-Log {
    param([string]$Text)
    $line = $Text
    if ($password) { $line = $line.Replace($password, '***') }
    Write-Host $line
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
}

function Add-Result {
    param([string]$Step, [bool]$Pass, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ Step = $Step; Pass = $Pass; Detail = $Detail })
    $mark = if ($Pass) { 'PASS' } else { 'FAIL' }
    Write-Log "=> $mark  $Step  ($Detail)"
}

# Runs a command, logs its output (with the password masked) and returns the exit code and the output lines.
function Invoke-Logged {
    param([string]$Title, [string]$Exe, [string[]]$Arguments, [switch]$Silent)
    Write-Log ''
    Write-Log "### $Title"
    Write-Log "> $Exe $($Arguments -join ' ')"
    $started = Get-Date
    $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    if (-not $Silent) { foreach ($l in $lines) { Write-Log $l } }
    else { Write-Log "(output not logged: $($lines.Count) lines)" }
    Write-Log ("exit code: $code, {0:N1}s" -f ((Get-Date) - $started).TotalSeconds)
    return [pscustomobject]@{ Code = $code; Lines = $lines }
}

function Test-Tool {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

Write-Log "windows-check $id, started $((Get-Date).ToUniversalTime().ToString('u'))"
Write-Log "Isolated names: engine $engineName, volume $volumeName, pool folder $poolRoot, port $port"
Write-Log "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)), OS $([Environment]::OSVersion.VersionString)"

if (-not (Test-Tool 'docker') -or -not (Test-Tool 'dotnet')) {
    Write-Log 'error: docker and dotnet must both be on the PATH.'
    exit 2
}

$null = Invoke-Logged 'docker version' 'docker' @('version', '--format', 'client {{.Client.Version}}, server {{.Server.Version}} on {{.Server.Os}}/{{.Server.Arch}}')
$info = Invoke-Logged 'docker info' 'docker' @('info', '--format', '{{.OperatingSystem}}; {{.OSType}}/{{.Architecture}}; WSL/VM kernel {{.KernelVersion}}')
if ($info.Code -ne 0) {
    Write-Log 'error: the Docker daemon is not reachable. Start Docker Desktop and use Linux containers.'
    exit 2
}
$null = Invoke-Logged 'dotnet --version' 'dotnet' @('--version')

# Everything below runs against the isolated names only.
# Other SQLBRANCH_* variables of the machine are cleared for the run and put back at the end, so they cannot
# change what is tested. An empty --config file stands in for any .sqlbranch.yml in a parent folder.
$savedEnv = @{}
foreach ($v in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'SQLBRANCH_*' })) {
    $savedEnv[$v.Name] = $v.Value
    Remove-Item "Env:\$($v.Name)" -ErrorAction SilentlyContinue
}
Set-Content -Path $configPath -Value '# no settings' -Encoding UTF8
$env:SQLBRANCH_HARNESS_ENGINE = $engineName
$env:SQLBRANCH_HARNESS_VOLUME = $volumeName
$env:SQLBRANCH_HARNESS_POOL_ROOT = $poolRoot
$env:SQLBRANCH_HARNESS_HOME = $homeDir
$env:SQLBRANCH_POOL_BACKEND = $PoolBackend
$env:SQLBRANCH_POOL_SIZE = $PoolSize
$env:SQLBRANCH_PORT = "$port"
$env:SQLBRANCH_SA_PASSWORD = $password

$created = $false
$initOk = $false
try {
    # 0. Build. The harness is the same command line as `sqlbranch`, pointed at the isolated names.
    $build = Invoke-Logged 'dotnet build' 'dotnet' @('build', (Join-Path $repo 'tests\SqlBranch.CliHarness\SqlBranch.CliHarness.csproj'), '-c', 'Release', '-o', $buildDir, '--nologo', '-v', 'q')
    Add-Result 'build' ($build.Code -eq 0) "exit $($build.Code)"
    if ($build.Code -ne 0) { throw 'build failed' }
    $dll = Join-Path $buildDir 'SqlBranch.CliHarness.dll'

    function Invoke-Sb {
        param([string]$Title, [string[]]$SbArgs, [switch]$Silent)
        return Invoke-Logged $Title 'dotnet' (@($dll) + $SbArgs + @('--config', $configPath)) -Silent:$Silent
    }

    # 1. init
    $created = $true
    $init = Invoke-Sb 'sqlbranch init' @('init')
    $mode = ($init.Lines | Where-Object { $_ -like 'Clone mode:*' } | Select-Object -First 1)
    Add-Result 'init' ($init.Code -eq 0) "exit $($init.Code), $mode"
    if ($init.Code -ne 0) { throw 'init failed' }
    $initOk = $true

    # 2. A small backup: from -BakPath, or made inside the new engine and copied out.
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    if (-not $BakPath) {
        $BakPath = Join-Path $workDir 'shop.bak'
        $env:SQLCMDPASSWORD = $password
        $sql = "CREATE DATABASE [Shop]; ALTER DATABASE [Shop] SET RECOVERY SIMPLE; " +
               "EXEC [Shop].sys.sp_executesql N'CREATE TABLE dbo.Item (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL); INSERT dbo.Item SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N''item'' FROM sys.all_objects'; " +
               "BACKUP DATABASE [Shop] TO DISK = N'/tmp/shop.bak' WITH INIT; DROP DATABASE [Shop];"
        $mk = Invoke-Logged 'make a backup in the engine' 'docker' @('exec', '-e', 'SQLCMDPASSWORD', $engineName, '/opt/mssql-tools18/bin/sqlcmd', '-C', '-b', '-S', 'localhost', '-U', 'sa', '-Q', $sql)
        Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
        $cp = Invoke-Logged 'copy the backup out' 'docker' @('cp', "${engineName}:/tmp/shop.bak", $BakPath)
        $null = Invoke-Logged 'remove the backup in the engine' 'docker' @('exec', $engineName, 'rm', '-f', '/tmp/shop.bak')
        Add-Result 'make backup' (($mk.Code -eq 0) -and ($cp.Code -eq 0) -and (Test-Path $BakPath)) "exit $($mk.Code)/$($cp.Code)"
    }

    # 3. image build
    $ib = Invoke-Sb 'sqlbranch image build' @('image', 'build', $image, '--from-bak', $BakPath)
    Add-Result 'image build' ($ib.Code -eq 0) "exit $($ib.Code)"
    if ($ib.Code -ne 0) { throw 'image build failed' }

    # 4. branch create, list, status
    $bc = Invoke-Sb 'sqlbranch branch create' @('branch', 'create', $branch, '--from', $image)
    $bcMode = ($bc.Lines | Where-Object { $_ -like 'Clone mode:*' } | Select-Object -First 1)
    Add-Result 'branch create' ($bc.Code -eq 0) "exit $($bc.Code), $bcMode"
    if ($bc.Code -ne 0) { throw 'branch create failed' }
    $bl = Invoke-Sb 'sqlbranch branch list' @('branch', 'list')
    Add-Result 'branch list' ($bl.Code -eq 0) "exit $($bl.Code)"
    $st = Invoke-Sb 'sqlbranch status' @('status')
    Add-Result 'status' ($st.Code -eq 0) "exit $($st.Code)"

    # 5. branch conn (not logged: it holds the password).
    $cn = Invoke-Sb 'sqlbranch branch conn --quiet' @('branch', 'conn', $branch, '--quiet') -Silent
    Add-Result 'branch conn' (($cn.Code -eq 0) -and ($cn.Lines.Count -ge 1)) "exit $($cn.Code)"

    # 5a. Can Windows reach the published engine port at all?
    $tcpOk = $false
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $tcp.BeginConnect('127.0.0.1', $port, $null, $null)
        $tcpOk = $pending.AsyncWaitHandle.WaitOne(10000) -and $tcp.Connected
        if ($tcpOk) { $tcp.EndConnect($pending) }
    }
    catch { Write-Log "TCP connect failed: $($_.Exception.Message)" }
    finally { $tcp.Close() }
    Add-Result 'TCP from Windows to 127.0.0.1 (engine port)' $tcpOk "port $port"

    # 5b. SELECT through the port from a separate container. The password goes in the environment of the
    #     container, never on the command line.
    $env:SQLCMDPASSWORD = $password
    $sel = Invoke-Logged 'SELECT 1 via sqlcmd in a separate container, to host.docker.internal' 'docker' @('run', '--rm', '--add-host', 'host.docker.internal:host-gateway', '-e', 'SQLCMDPASSWORD', '--entrypoint', '/opt/mssql-tools18/bin/sqlcmd', $sqlcmdImage,
        '-C', '-b', '-l', '20', '-S', "host.docker.internal,$port", '-U', 'sa', '-d', $branch, '-Q', 'SELECT 1 AS one; SELECT COUNT(*) AS items FROM dbo.Item')
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
    Add-Result 'SELECT via host.docker.internal (separate container)' ($sel.Code -eq 0) "exit $($sel.Code)"
    if ($sel.Code -ne 0) {
        # Say which part failed: check the branch from inside the engine.
        $env:SQLCMDPASSWORD = $password
        $in = Invoke-Logged 'SELECT inside the engine (fallback, does not test the port from outside)' 'docker' @('exec', '-e', 'SQLCMDPASSWORD', $engineName, '/opt/mssql-tools18/bin/sqlcmd', '-C', '-b', '-S', 'localhost', '-U', 'sa', '-d', $branch, '-Q', 'SELECT COUNT(*) AS items FROM dbo.Item')
        Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
        Add-Result 'SELECT inside the engine (fallback)' ($in.Code -eq 0) "exit $($in.Code)"
    }

    # 6. branch rm
    $br = Invoke-Sb 'sqlbranch branch rm' @('branch', 'rm', $branch)
    Add-Result 'branch rm' ($br.Code -eq 0) "exit $($br.Code)"
}
catch {
    Write-Log "stopped: $($_.Exception.Message)"
}
finally {
    # Tear down only what this run created.
    if ($created -and (Test-Path (Join-Path $buildDir 'SqlBranch.CliHarness.dll'))) {
        $td = Invoke-Logged 'sqlbranch teardown --force' 'dotnet' @((Join-Path $buildDir 'SqlBranch.CliHarness.dll'), 'teardown', '--force')
        # When init itself failed there may be nothing to remove: exit 3 not_initialized is then fine.
        $tdOk = ($td.Code -eq 0) -or ((-not $initOk) -and ($td.Code -eq 3))
        Add-Result 'teardown' $tdOk "exit $($td.Code)"
    }

    # The empty folder the pool file lived in, in the Docker host (as the integration tests do).
    $null = Invoke-Logged 'remove the empty pool folder' 'docker' @('run', '--rm', '-v', '/var/lib/sqlbranch:/var/lib/sqlbranch', $helperImage, 'sh', '-c',
        "for i in 1 2 3 4 5; do rmdir '$poolRoot/mnt' 2>/dev/null; rmdir '$poolRoot' && break; sleep 1; done; ls -A /var/lib/sqlbranch")

    # The pool folder itself must be gone from the Docker host.
    $leftP = Invoke-Logged 'pool folder left' 'docker' @('run', '--rm', '-v', '/var/lib/sqlbranch:/var/lib/sqlbranch', $helperImage, 'sh', '-c',
        "if [ -e '$poolRoot' ]; then echo present; else echo gone; fi")
    $poolGone = (@($leftP.Lines | Where-Object { $_ -eq 'gone' }).Count -eq 1) -and ($leftP.Code -eq 0)

    # Nothing of this run may be left.
    $leftC = Invoke-Logged 'containers left' 'docker' @('ps', '-a', '--filter', "name=$engineName", '--format', '{{.Names}}')
    $leftV = Invoke-Logged 'volumes left' 'docker' @('volume', 'ls', '--filter', "name=$volumeName", '--format', '{{.Name}}')
    $clean = (@($leftC.Lines | Where-Object { $_ }).Count -eq 0) -and (@($leftV.Lines | Where-Object { $_ }).Count -eq 0) -and $poolGone
    Add-Result 'nothing left' $clean 'no container, volume or pool folder of this run remains'

    Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    Remove-Item -Force $configPath -ErrorAction SilentlyContinue
    foreach ($v in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'SQLBRANCH_*' })) {
        Remove-Item "Env:\$($v.Name)" -ErrorAction SilentlyContinue
    }
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
    foreach ($k in $savedEnv.Keys) { Set-Item "Env:\$k" $savedEnv[$k] }
}

Write-Log ''
Write-Log '=== Summary ==='
foreach ($r in $script:results) {
    $mark = if ($r.Pass) { 'PASS' } else { 'FAIL' }
    Write-Log ("{0}  {1}  {2}" -f $mark, $r.Step, $r.Detail)
}
$failed = @($script:results | Where-Object { -not $_.Pass }).Count
Write-Log "Log file: $LogPath"
if ($failed -eq 0) { exit 0 } else { exit 1 }
