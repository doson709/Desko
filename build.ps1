# Builds Desko into .\dist, ready to zip and share:
#   Desko.exe, DeskoGuard.exe (watchdog), setup.bat, uninstall.bat, guide.
# Needs the .NET 8 SDK; setup.bat installs the .NET 8 Desktop Runtime on machines that lack it.
$ErrorActionPreference = 'Stop'
# A running copy is asked to exit normally first: it puts its parked icons back itself. Killing it would make its
# watchdog do that instead, possibly while the new build is already starting.
if ((Get-Process Desko -ErrorAction SilentlyContinue) -and (Test-Path "$PSScriptRoot\dist\Desko.exe")) {
    Start-Process "$PSScriptRoot\dist\Desko.exe" -ArgumentList "--exit" -Wait
}
Get-Process Desko, DeskoGuard -ErrorAction SilentlyContinue | Stop-Process -Force
# Start from an empty folder so nothing from an older build (or the pre-2.0 DeskFences name) ends up in the zip.
Remove-Item "$PSScriptRoot\dist" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot\Desko.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o "$PSScriptRoot\dist"

# The watchdog is Native AOT (~1-2 MB of memory) when the Windows SDK is installed for the C++ linker,
# otherwise a regular small .NET build.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
$guard = "$PSScriptRoot\Guard\DeskoGuard.csproj"
if (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Lib") {
    dotnet publish $guard -c Release -r win-x64 -o "$PSScriptRoot\dist"
} else {
    dotnet publish $guard -c Release -r win-x64 --self-contained false -p:PublishAot=false `
        -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o "$PSScriptRoot\dist"
}

Remove-Item "$PSScriptRoot\dist\*.pdb" -ErrorAction SilentlyContinue
Copy-Item "$PSScriptRoot\installer\*" "$PSScriptRoot\dist" -Force
Write-Host "Done: $PSScriptRoot\dist\Desko.exe"
