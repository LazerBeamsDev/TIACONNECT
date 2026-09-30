@echo off
rem ANet fork of Czarnak/tia-portal-mcp - build, test, pack and install as global tool "tia-mcp".
rem Double-click or run from cmd. Log: anet-build.log next to this file (Claude reads it).
setlocal EnableExtensions
cd /d "%~dp0"
set /p VER=<anet-version.txt
set LOG=%~dp0anet-build.log
set TIADIR=C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48
echo ANet build %VER% - prubeh se zapisuje do anet-build.log, okno nezavirej.
echo ==== ANet build %VER% %date% %time% > "%LOG%"
dotnet --list-sdks >> "%LOG%" 2>&1
echo ---- PublicAPI V21 (Unified DLL check) >> "%LOG%"
dir /b "%TIADIR%" >> "%LOG%" 2>&1
dotnet --list-sdks | findstr /b "10.0.4 10.0.5 10.0.6 10.0.7 10.0.8 10.0.9" >nul
if errorlevel 1 (
  echo .NET SDK 10.0.400+ chybi - instaluji pres winget, potvrd pripadne okno UAC...
  echo ---- winget install Microsoft.DotNet.SDK.10 >> "%LOG%"
  winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements >> "%LOG%" 2>&1
  dotnet --list-sdks >> "%LOG%" 2>&1
)
echo [1/5] restore balicku (prvni beh i nekolik minut)...
echo ---- restore >> "%LOG%"
dotnet restore TiaMcpServer.sln >> "%LOG%" 2>&1 || goto fail
echo [2/5] build...
echo ---- build >> "%LOG%"
dotnet build TiaMcpServer.sln -m:1 -c Release --no-restore "/p:TiaPortalV21Dir=%TIADIR%" /p:EnableSourceLink=false /p:EnableSourceControlManagerQueries=false >> "%LOG%" 2>&1 || goto fail
echo [3/5] testy...
echo ---- test (failures are logged, not fatal) >> "%LOG%"
dotnet test TiaMcpServer.Tests -c Release --no-build --logger "console;verbosity=minimal" >> "%LOG%" 2>&1
echo TESTEXIT=%ERRORLEVEL% >> "%LOG%"
echo [4/5] pack...
echo ---- pack >> "%LOG%"
dotnet pack TiaMcpServer\TiaMcpServer.csproj -c Release --no-restore -o artifacts "/p:TiaPortalV21Dir=%TIADIR%" /p:Version=%VER% /p:PackageVersion=%VER% /p:InformationalVersion=%VER% /p:IncludeSourceRevisionInInformationalVersion=false /p:EnableSourceLink=false /p:EnableSourceControlManagerQueries=false >> "%LOG%" 2>&1 || goto fail
echo [5/5] instalace tia-mcp a doctor...
echo ---- install >> "%LOG%"
taskkill /IM tia-mcp.exe /F >> "%LOG%" 2>&1
taskkill /IM TiaMcpServer.OpennessWorker.exe /F >> "%LOG%" 2>&1
dotnet tool uninstall -g TiaMcpServer >> "%LOG%" 2>&1
dotnet tool install -g --add-source artifacts TiaMcpServer --version %VER% >> "%LOG%" 2>&1 || goto fail
tia-mcp doctor --access-mode read-only >> "%LOG%" 2>&1
echo RESULT=OK >> "%LOG%"
echo Hotovo - RESULT=OK. Restartuj Claude appku.
pause
exit /b 0
:fail
echo RESULT=FAIL >> "%LOG%"
echo CHYBA - viz anet-build.log
pause
exit /b 1
