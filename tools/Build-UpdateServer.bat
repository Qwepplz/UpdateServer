@echo off
setlocal
cd /d "%~dp0"

set "VERSION=1.0.0"
set "ROOT_DIR=%~dp0.."
set "SRC_FILE=%ROOT_DIR%\src\UpdateServer\Program.cs"
set "OUT_DIR=%ROOT_DIR%\dist"
set "OUT_FILE=%OUT_DIR%\UpdateServer.exe"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REFDIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "SIGN_CERT="
set "SIGN_PASSWORD="
set "SIGNTOOL="
set "TIMESTAMP_URL=http://timestamp.digicert.com"
set "NO_PAUSE=%BUILD_NO_PAUSE%"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--help" goto usage
if /i "%~1"=="/?" goto usage
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto parse_args
)
if not defined SIGN_CERT (
    set "SIGN_CERT=%~1"
    shift
    goto parse_args
)
if not defined SIGN_PASSWORD (
    set "SIGN_PASSWORD=%~1"
    shift
    goto parse_args
)
echo Unknown argument: %~1
set "EXIT_CODE=1"
goto finish

:args_done
if not exist "%CSC%" (
    set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    set "REFDIR=C:\Windows\Microsoft.NET\Framework\v4.0.30319"
)

if not exist "%CSC%" (
    echo C# compiler was not found.
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%SRC_FILE%" (
    echo Source file was not found:
    echo %SRC_FILE%
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

echo Building UpdateServer v%VERSION%...
"%CSC%" ^
  /nologo ^
  /target:exe ^
  /out:"%OUT_FILE%" ^
  /optimize+ ^
  /debug- ^
  /r:"%REFDIR%\System.Web.Extensions.dll" ^
  /r:"%REFDIR%\System.Core.dll" ^
  "%SRC_FILE%"

set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo Build failed. Exit code: %EXIT_CODE%
    goto finish
)

echo Build completed: %OUT_FILE%

if not defined SIGN_CERT (
    echo Signing skipped. Pass a .pfx certificate path to sign the EXE.
    goto finish
)

call :find_signtool
if not defined SIGNTOOL (
    echo SignTool was not found. Install Windows SDK or add signtool.exe to PATH.
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%SIGN_CERT%" (
    echo Signing certificate was not found:
    echo %SIGN_CERT%
    set "EXIT_CODE=1"
    goto finish
)

echo Signing with: %SIGN_CERT%
if defined SIGN_PASSWORD (
    "%SIGNTOOL%" sign /fd SHA256 /tr "%TIMESTAMP_URL%" /td SHA256 /f "%SIGN_CERT%" /p "%SIGN_PASSWORD%" "%OUT_FILE%"
) else (
    "%SIGNTOOL%" sign /fd SHA256 /tr "%TIMESTAMP_URL%" /td SHA256 /f "%SIGN_CERT%" "%OUT_FILE%"
)

set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" (
    echo Signing completed.
) else (
    echo Signing failed. Exit code: %EXIT_CODE%
)
goto finish

:find_signtool
for %%I in (signtool.exe) do if not "%%~$PATH:I"=="" set "SIGNTOOL=%%~$PATH:I"
if defined SIGNTOOL exit /b 0

for %%I in ("%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\signtool.exe") do (
    if exist "%%~fI" set "SIGNTOOL=%%~fI"
)
if defined SIGNTOOL exit /b 0

for %%I in ("%ProgramFiles(x86)%\Windows Kits\10\bin\*\x86\signtool.exe") do (
    if exist "%%~fI" set "SIGNTOOL=%%~fI"
)
if defined SIGNTOOL exit /b 0

if exist "%ProgramFiles(x86)%\Windows Kits\8.1\bin\x64\signtool.exe" (
    set "SIGNTOOL=%ProgramFiles(x86)%\Windows Kits\8.1\bin\x64\signtool.exe"
)
exit /b 0

:usage
echo Usage:
echo   Build-UpdateServer.bat [--no-pause] [cert.pfx] [password]
echo.
echo Examples:
echo   Build-UpdateServer.bat
echo   Build-UpdateServer.bat --no-pause
echo   Build-UpdateServer.bat "C:\certs\code-signing.pfx" "password"
echo.
set "EXIT_CODE=0"
goto finish

:finish
echo.
if not defined NO_PAUSE pause
exit /b %EXIT_CODE%
