#Requires -Version 7.0
param(
    [string]$Config = '',
    [int]$Clients,
    [double]$Rate,
    [int]$PayloadBytes,
    [double]$Warmup,
    [double]$Duration,
    [int]$KestrelPort,
    [int]$HttpListenerPort,
    [string]$Room,
    [ValidateSet('both','kestrel','httplistener')][string]$Endpoints,
    [string]$Dotnet = '',
    [string]$Results = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Config) { $Config = Join-Path $repo 'relaytools.json' }
$Config = [IO.Path]::GetFullPath($Config)
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$mapping = @{ Clients='ClientsPerServer'; Rate='MessagesPerSecondPerClient'; PayloadBytes='PayloadBytes'; Warmup='WarmupSeconds'; Duration='DurationSeconds'; KestrelPort='KestrelPort'; HttpListenerPort='HttpListenerPort'; Room='Room'; Endpoints='Endpoints' }
$overrides = [Collections.Generic.List[string]]::new()
$flags = @{ Clients='--clients'; Rate='--rate'; PayloadBytes='--payload-bytes'; Warmup='--warmup'; Duration='--duration'; KestrelPort='--kestrel-port'; HttpListenerPort='--httplistener-port'; Room='--room'; Endpoints='--endpoints' }
foreach ($key in $mapping.Keys) {
    if ($PSBoundParameters.ContainsKey($key)) {
        $settings.($mapping[$key]) = $PSBoundParameters[$key]
        $overrides.Add($flags[$key])
        $overrides.Add([Convert]::ToString($PSBoundParameters[$key], [Globalization.CultureInfo]::InvariantCulture))
    }
}
$Endpoints = $settings.Endpoints
if (-not $Dotnet) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { $Dotnet = $installed.Source }
    elseif (Test-Path "$env:USERPROFILE/.dotnet/dotnet.exe") { $Dotnet = "$env:USERPROFILE/.dotnet/dotnet.exe" }
    elseif (Test-Path "$env:ProgramFiles/dotnet/dotnet.exe") { $Dotnet = "$env:ProgramFiles/dotnet/dotnet.exe" }
    else { throw 'The .NET SDK was not found. Pass -Dotnet with its absolute path.' }
}
if (-not $SkipBuild) {
    & $Dotnet build "$repo/RelayTools.slnx" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
$runId = [guid]::NewGuid().ToString('N')
if (-not $Results) { $Results = Join-Path $repo "results/$runId" }
$Results = [IO.Path]::GetFullPath($Results)
if ((Test-Path $Results) -and (Get-ChildItem $Results -Force | Select-Object -First 1)) {
    throw 'Results directory must be empty for a new run.'
}
[IO.Directory]::CreateDirectory($Results) | Out-Null
$common = @('--config',$Config,'--run-id',$runId,'--results',$Results) + $overrides.ToArray()
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$exitCodes = @{}
$forced = [Collections.Generic.List[int]]::new()
function Start-Child([string]$Mode, [string]$Assembly) {
    # ArgumentList 避免路徑或房名被 shell 重新解讀。
    [System.Diagnostics.ProcessStartInfo]$local:relayToolsChildStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $relayToolsChildStartInfo.FileName = $Dotnet
    $relayToolsChildStartInfo.UseShellExecute = $false
    $relayToolsChildStartInfo.CreateNoWindow = $true
    $relayToolsChildStartInfo.WorkingDirectory = $repo
    $relayToolsChildStartInfo.RedirectStandardOutput = $true
    $relayToolsChildStartInfo.RedirectStandardError = $true
    $relayToolsChildStartInfo.ArgumentList.Add($Assembly)
    $relayToolsChildStartInfo.ArgumentList.Add('--mode')
    $relayToolsChildStartInfo.ArgumentList.Add($Mode)
    foreach ($value in $common) { $relayToolsChildStartInfo.ArgumentList.Add($value) }
    $child = [System.Diagnostics.Process]::Start($relayToolsChildStartInfo)
    $children.Add($child)
    # 非同步讀取避免輸出 pipe 阻塞，日誌主體仍由 NLog 寫入。
    $child | Add-Member NoteProperty CapturedOutput $child.StandardOutput.ReadToEndAsync()
    $child | Add-Member NoteProperty CapturedError $child.StandardError.ReadToEndAsync()
    Write-Host "$Mode PID=$($child.Id)"
    return $child
}
try {
    Write-Host "runId=$runId results=$Results"
    Write-Host "config=$Config effective=$($settings | ConvertTo-Json -Compress)"
    $modes = if ($Endpoints -eq 'both') { @('kestrel','httplistener') } else { @($Endpoints) }
    foreach ($mode in $modes) {
        $project = if ($mode -eq 'kestrel') { 'RelayTools.KestrelServer' } else { 'RelayTools.HttpListenerServer' }
        $null = Start-Child $mode "$repo/src/$project/bin/Release/net10.0/$project.dll"
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    foreach ($mode in $modes) {
        while (-not (Test-Path (Join-Path $Results "$mode.ready"))) {
            if (($children | Where-Object HasExited) -or [DateTime]::UtcNow -gt $deadline) { throw "Server startup failed: $mode. Inspect results logs." }
            Start-Sleep -Milliseconds 100
        }
    }
    $client = Start-Child 'client' "$repo/src/RelayTools.Client/bin/Release/net10.0/RelayTools.Client.dll"
    while (-not $client.HasExited) {
        if ($children | Where-Object { $_.Id -ne $client.Id -and $_.HasExited }) { throw 'A server exited before the client.' }
        Start-Sleep -Milliseconds 200
    }
    if ($client.ExitCode -ne 0) { throw "Client failed with exit code $($client.ExitCode). Inspect results logs." }
}
finally {
    [IO.File]::WriteAllText((Join-Path $Results 'stop.txt'), 'Stop requested by launcher')
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    foreach ($child in $children) {
        while (-not $child.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (-not $child.HasExited) {
            Write-Warning "Forcing owned child PID=$($child.Id) to stop."
            $forced.Add($child.Id)
            $child.Kill($true)
            $child.WaitForExit()
        }
        $exitCodes["$($child.Id)"] = $child.ExitCode
        [IO.File]::WriteAllText((Join-Path $Results "$($child.Id).stdout.txt"), $child.CapturedOutput.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $Results "$($child.Id).stderr.txt"), $child.CapturedError.GetAwaiter().GetResult())
        Write-Host "Stopped owned PID=$($child.Id) exitCode=$($child.ExitCode)"
        $child.Dispose()
    }
    @{ runId=$runId; arguments=$common; exitCodes=$exitCodes; forcedPids=@($forced.ToArray()); results=$Results } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Results 'run-summary.json') -Encoding utf8
    Write-Host "Logs preserved: $Results"
}
if ($exitCodes.Values | Where-Object { $_ -ne 0 }) { throw 'One or more children failed.' }
