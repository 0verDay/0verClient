@echo off
setlocal
title 0verClient Test Pack
echo.
echo  ==========================================
echo   0verClient test pack is running
echo  ==========================================
echo.
echo  This file was downloaded from your own server by 0verClient,
echo  checked against its sha256 from the manifest, then launched.
echo.
echo  Working directory : %CD%
echo  Executable        : %~f0
echo  Computer          : %COMPUTERNAME%
echo  Started at        : %DATE% %TIME%
echo.

rem Do NOT put a bare ")" inside a parenthesised block: cmd.exe treats it as the end of
rem the block, which silently truncates the line and makes the else branch run as well.
rem Hence the single-line "if exist ... for ... do set" below, and no parentheses in text.
rem %~dp0 is used so the payload is found even when the working directory is elsewhere.
set "PAYLOADFILE=%~dp0content\hello.txt"
set "PAYLOADINFO=content\hello.txt  --  not found, the install may be incomplete"
if exist "%PAYLOADFILE%" for %%A in ("%PAYLOADFILE%") do set "PAYLOADINFO=content\hello.txt  --  %%~zA bytes"
echo  Payload           : %PAYLOADINFO%
echo.
echo  If you can read this, the whole chain works:
echo    index.json -^> manifest.json -^> download -^> sha256 verify
echo    -^> staging -^> atomic commit -^> process launch
echo.
pause
endlocal
