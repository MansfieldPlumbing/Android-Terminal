Write-Host "Building React Frontend..." -ForegroundColor Cyan
Set-Location $PSScriptRoot\src\terminal
npm install
npm run build
Set-Location $PSScriptRoot

Write-Host "Syncing Assets to Android Backend..." -ForegroundColor Cyan
if (Test-Path "src\runspace\Assets\wwwroot") { Remove-Item -Recurse -Force "src\runspace\Assets\wwwroot" }
New-Item -ItemType Directory -Force "src\runspace\Assets\wwwroot" | Out-Null
Copy-Item -Path "src\terminal\dist\*" -Destination "src\runspace\Assets\wwwroot\" -Recurse -Force

Write-Host "Forcefully clearing previous installs from all users..." -ForegroundColor Yellow
& "C:\bin\SCRCPY\adb.exe" uninstall com.mansfieldplumbing.terminal | Out-Null
& "C:\bin\SCRCPY\adb.exe" shell pm uninstall --user 0 com.mansfieldplumbing.terminal | Out-Null

Write-Host "Compiling and Pushing to Device via ADB..." -ForegroundColor Green
Set-Location src\runspace
dotnet build -c Release -t:Run -f net11.0-android /p:JavaSdkDirectory="C:\bin\AndroidStudio\jbr" /p:AdbExe="C:\bin\SCRCPY\adb.exe"
Set-Location $PSScriptRoot
