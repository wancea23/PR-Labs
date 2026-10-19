param([Parameter(Mandatory = $true)][string]$Drive)
$Drive = $Drive.TrimEnd(':', '\') + ":"
$data = "$env:USERPROFILE\prlab1-data"
$exe = "$data\build\bin\Lab1\release\Lab1.exe"
dotnet build "$PSScriptRoot" -c Release --artifacts-path "$data\build" | Out-Null
$dir = "$Drive\prlab1"
$out = "$PSScriptRoot\results\task2.txt"
New-Item -ItemType Directory -Force "$PSScriptRoot\results" | Out-Null
if (-not (Test-Path "$dir\numbers_8.txt")) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item "$data\parts\numbers_*.txt" $dir
}
foreach ($t in 1, 2, 4) {
    Write-Host "Eject $Drive, unplug it and plug it back in. Next run: $t thread(s)"
    while ([IO.File]::Exists("$dir\numbers_8.txt")) { Start-Sleep -Milliseconds 200 }
    Write-Host "Drive removed"
    while (-not [IO.File]::Exists("$dir\numbers_8.txt")) { Start-Sleep -Milliseconds 200 }
    Write-Host "Drive back, measuring $t thread(s)"
    Start-Sleep -Seconds 3
    & $exe 2 $dir $t | Tee-Object -FilePath $out -Append
    [console]::Beep(1000, 600)
}
