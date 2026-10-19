param([string]$Numbers = "$env:USERPROFILE\prlab1-data\numbers.txt", [int]$Rounds = 5)
$exe = "$env:USERPROFILE\prlab1-data\build\bin\Lab1\release\Lab1.exe"
dotnet build "$PSScriptRoot" -c Release --artifacts-path "$env:USERPROFILE\prlab1-data\build" | Out-Null
$out = "$PSScriptRoot\results\task1.txt"
New-Item -ItemType Directory -Force "$PSScriptRoot\results" | Out-Null
foreach ($round in 1..$Rounds) {
    foreach ($t in 1, 2, 4, 8, 16, 32, 64) { & $exe 1a $Numbers $t | Tee-Object -FilePath $out -Append }
    & $exe 1b $Numbers | Tee-Object -FilePath $out -Append
    foreach ($t in 1, 2, 4, 8, 16, 32, 64) { & $exe 1c $Numbers $t | Tee-Object -FilePath $out -Append }
}
