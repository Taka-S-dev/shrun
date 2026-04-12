@echo off
setlocal

set DRIVE=Z:
set TARGET=%~dp0test_root

mkdir "%TARGET%\task-a1" 2>nul
mkdir "%TARGET%\task-a2" 2>nul
mkdir "%TARGET%\task-b1" 2>nul
mkdir "%TARGET%\task-b2" 2>nul

subst %DRIVE% "%TARGET%"

cmd /k "cd /d %DRIVE%\"
