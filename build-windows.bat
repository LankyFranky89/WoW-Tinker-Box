@echo off
echo Building WoW Tinker Box...
dotnet publish WoWTinkerBox.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
  echo.
  echo Build failed. Is the .NET 8 SDK installed? Get it from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo.
echo Done! Your app is in the "publish" folder: publish\WoWTinkerBox.exe
pause
