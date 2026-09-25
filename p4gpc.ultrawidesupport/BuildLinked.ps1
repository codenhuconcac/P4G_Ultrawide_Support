# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/p4gpc.ultrawidesupport/*" -Force -Recurse
dotnet publish "./p4gpc.ultrawidesupport.csproj" -c Release -o "$env:RELOADEDIIMODS/p4gpc.ultrawidesupport" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location