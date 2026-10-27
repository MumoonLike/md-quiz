@echo off
rem MdQuiz teacher: allow inbound connections through Windows Firewall.
rem Right-click this file and choose "Run as administrator".
rem Put this file in the same folder as MdQuiz.Teacher.exe.

net session >nul 2>&1
if errorlevel 1 (
    echo Please right-click this file and choose "Run as administrator".
    pause
    exit /b 1
)

set "EXE=%~dp0MdQuiz.Teacher.exe"
if not exist "%EXE%" (
    echo MdQuiz.Teacher.exe was not found in %~dp0
    echo Please copy this file into the teacher program folder.
    pause
    exit /b 1
)

netsh advfirewall firewall delete rule name="MdQuiz Teacher" >nul 2>&1
netsh advfirewall firewall delete rule name="MdQuiz Teacher TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="MdQuiz Teacher UDP" >nul 2>&1

netsh advfirewall firewall add rule name="MdQuiz Teacher" dir=in action=allow program="%EXE%" enable=yes profile=any
netsh advfirewall firewall add rule name="MdQuiz Teacher TCP" dir=in action=allow protocol=TCP localport=47320 profile=any
netsh advfirewall firewall add rule name="MdQuiz Teacher UDP" dir=in action=allow protocol=UDP localport=47321 profile=any

echo.
echo Done. Students can now connect to this computer.
pause
