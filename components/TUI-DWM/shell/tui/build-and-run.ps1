Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Building modern TUI-DWM engine..." -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# Build the modern projects inside src/
dotnet build src/TuiDwm.Engine/TuiDwm.Engine.csproj -c Debug
dotnet build src/TuiDwm.Plugins/TuiDwm.Plugins.Clock/TuiDwm.Plugins.Clock.csproj -c Debug
dotnet build src/TuiDwm.Plugins/TuiDwm.Plugins.SysInfo/TuiDwm.Plugins.SysInfo.csproj -c Debug
dotnet build src/TuiDwm.Plugins/TuiDwm.Plugins.Terminal/TuiDwm.Plugins.Terminal.csproj -c Debug

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Moving EXE and files to root working directory..." -ForegroundColor Yellow

# Copy everything out of the maze and into the root
Copy-Item -Path "src\TuiDwm.Engine\bin\Debug\net11.0\*" -Destination . -Recurse -Force

# Setup plugins folder at the root
if (-not (Test-Path "plugins")) { New-Item -ItemType Directory -Path "plugins" -Force | Out-Null }

# Move the plugins
Copy-Item "src\TuiDwm.Plugins\TuiDwm.Plugins.Clock\bin\Debug\net11.0\TuiDwm.Plugins.Clock.dll" -Destination "plugins\" -Force
Copy-Item "src\TuiDwm.Plugins\TuiDwm.Plugins.SysInfo\bin\Debug\net11.0\TuiDwm.Plugins.SysInfo.dll" -Destination "plugins\" -Force
Copy-Item "src\TuiDwm.Plugins\TuiDwm.Plugins.Terminal\bin\Debug\net11.0\TuiDwm.Plugins.Terminal.dll" -Destination "plugins\" -Force

Write-Host "
Ready! Launching .\TuiDwm.Engine.exe directly in Windows Terminal..." -ForegroundColor Green
.\TuiDwm.Engine.exe