@echo off
setlocal
set "APP_DIR=%~dp0"
set "EXE_PATH=%APP_DIR%PonceanoFacturador.exe"

if not exist "%EXE_PATH%" (
	echo No se encontro PonceanoFacturador.exe en esta carpeta.
	echo Descomprime el ZIP completo y vuelve a ejecutar este archivo.
	pause
	exit /b 1
)

set "PF_EXE=%EXE_PATH%"
set "PF_DIR=%APP_DIR%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $desktop=[Environment]::GetFolderPath('DesktopDirectory'); if ([string]::IsNullOrWhiteSpace($desktop)) { $desktop=[Environment]::GetFolderPath('Desktop') }; $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'Ponceano Facturador.lnk')); $shortcut.TargetPath=$env:PF_EXE; $shortcut.WorkingDirectory=$env:PF_DIR; $shortcut.IconLocation=$env:PF_EXE + ',0'; $shortcut.Save()"
if errorlevel 1 (
	echo No se pudo crear el acceso directo. Comprueba que PowerShell esta disponible.
	pause
	exit /b 1
)

echo Acceso directo creado en el Escritorio.
echo Para fijarlo a la barra de tareas, consulta LEEME_INSTALAR_ACCESOS.txt.
pause
