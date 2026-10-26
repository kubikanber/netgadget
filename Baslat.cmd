@echo off
rem Ag Etkinligi gadget'ini baslatir. Yonetici izni istenir; reddedilirse sinirli modda acilir.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0NetGadget.ps1"
