@echo off
title DocMaster Pro Baslatici
echo ===================================================
echo     DocMaster Pro (PDFium Destekli Guncel Surum)
echo ===================================================
echo.
cd /d "%~dp0DocMasterPro\desktop-app\bin\Debug\net8.0-windows"

if not exist "DocConverter.exe" (
    echo [HATA] DocConverter.exe bulunamadi!
    echo Proje derleniyor, lutfen bekleyin...
    cd /d "%~dp0"
    dotnet build "DocMasterPro\DocMasterPro.sln"
    cd /d "%~dp0DocMasterPro\desktop-app\bin\Debug\net8.0-windows"
)

echo Uygulama baslatiliyor...
start "" "DocConverter.exe"
exit
