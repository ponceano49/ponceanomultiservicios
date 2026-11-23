param(
	[Parameter(Mandatory = $true)][string]$Apk,
	[Parameter(Mandatory = $true)][string]$Dispositivo,
	[Parameter(Mandatory = $true)][string]$Almacenamiento,
	[Parameter(Mandatory = $true)][string]$Carpeta,
	[switch]$ProcesoMtp,
	[string]$Temporal,
	[int]$TiempoLimiteSegundos = 180
)

$ErrorActionPreference = 'Stop'

if (-not $ProcesoMtp) {
	$Temporal = Join-Path ([System.IO.Path]::GetTempPath()) ('PonceanoApk-' + [guid]::NewGuid().ToString('N'))
	$valores = @($PSCommandPath, $Apk, $Dispositivo, $Almacenamiento, $Carpeta, $Temporal)
	if (@($valores | Where-Object { $_.Contains('"') }).Count -gt 0) {
		Write-Output 'No se pudo copiar el APK: un argumento contiene comillas no admitidas.'
		exit 1
	}
	$inicio = New-Object System.Diagnostics.ProcessStartInfo
	$inicio.FileName = Join-Path $PSHOME 'powershell.exe'
	$inicio.Arguments = '-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -Apk "{1}" -Dispositivo "{2}" -Almacenamiento "{3}" -Carpeta "{4}" -Temporal "{5}" -ProcesoMtp' -f $valores
	$inicio.UseShellExecute = $false
	$inicio.CreateNoWindow = $true
	$inicio.RedirectStandardOutput = $true
	$inicio.RedirectStandardError = $true
	$proceso = $null
	try {
		$proceso = [System.Diagnostics.Process]::Start($inicio)
		if (-not $proceso.WaitForExit($TiempoLimiteSegundos * 1000)) {
			$proceso.Kill()
			$proceso.WaitForExit()
			Write-Output 'La copia MTP excedio el tiempo limite. Compruebe el telefono y copie el APK manualmente.'
			exit 1
		}
		$salida = $proceso.StandardOutput.ReadToEnd()
		$errores = $proceso.StandardError.ReadToEnd()
		if ($salida) { Write-Output $salida.TrimEnd() }
		if ($errores) { Write-Output $errores.TrimEnd() }
		exit $proceso.ExitCode
	}
	catch {
		Write-Output ("No se pudo ejecutar la copia MTP: " + $_.Exception.Message)
		exit 1
	}
	finally {
		if ($null -ne $proceso) { $proceso.Dispose() }
		if (Test-Path -LiteralPath $Temporal) {
			$copiaTemporal = Join-Path $Temporal 'com.companyname.ponceanofacturador-Signed.apk'
			if (Test-Path -LiteralPath $copiaTemporal) { Remove-Item -LiteralPath $copiaTemporal -Force -ErrorAction Continue }
			Remove-Item -LiteralPath $Temporal -Force -ErrorAction Continue
		}
	}
}

function ObtenerElementoUnico($elementos, [string]$nombre, [string]$descripcion) {
	$coincidencias = @($elementos | Where-Object { $_.Name -eq $nombre })
	if ($coincidencias.Count -ne 1) {
		throw "No se encontro exactamente un $descripcion llamado '$nombre'."
	}
	return $coincidencias[0]
}

try {
	$archivo = Get-Item -LiteralPath $Apk
	if ($archivo.PSIsContainer -or $archivo.Name -ne 'com.companyname.ponceanofacturador-Signed.apk') {
		throw 'El archivo indicado no es el APK firmado esperado.'
	}
	$hashOrigen = (Get-FileHash -LiteralPath $archivo.FullName -Algorithm SHA256).Hash
	$shell = New-Object -ComObject Shell.Application
	$pc = $shell.NameSpace(17)
	if ($null -eq $pc) { throw 'No se pudo acceder a Este equipo.' }
	$telefono = ObtenerElementoUnico $pc.Items() $Dispositivo 'dispositivo MTP'
	$unidad = ObtenerElementoUnico $telefono.GetFolder.Items() $Almacenamiento 'almacenamiento MTP'
	$destino = ObtenerElementoUnico $unidad.GetFolder.Items() $Carpeta 'carpeta de destino'
	if (-not $destino.IsFolder) { throw 'El destino MTP no es una carpeta.' }
	$carpetaDestino = $destino.GetFolder

	$null = New-Item -ItemType Directory -Path $Temporal
	$copiaLocal = Join-Path $Temporal $archivo.Name
	$carpetaLocal = $shell.NameSpace($Temporal)
	if ($null -eq $carpetaLocal) { throw 'No se pudo abrir la carpeta temporal de verificacion.' }

	$carpetaDestino.CopyHere($archivo.FullName, 20)
	$limite = [DateTime]::UtcNow.AddSeconds(150)
	while ([DateTime]::UtcNow -lt $limite) {
		Start-Sleep -Seconds 3
		$candidatos = @($carpetaDestino.Items() | Where-Object {
			-not $_.IsFolder -and ($_.Name -eq $archivo.Name -or $_.Name -eq [System.IO.Path]::GetFileNameWithoutExtension($archivo.Name))
		})
		if ($candidatos.Count -gt 1) { throw 'Hay mas de un APK con el mismo nombre en la carpeta del telefono.' }
		if ($candidatos.Count -eq 0) { continue }
		if (Test-Path -LiteralPath $copiaLocal) { Remove-Item -LiteralPath $copiaLocal -Force }
		$carpetaLocal.CopyHere($candidatos[0], 20)
		$esperaLectura = [DateTime]::UtcNow.AddSeconds(30)
		while ([DateTime]::UtcNow -lt $esperaLectura -and [DateTime]::UtcNow -lt $limite) {
			Start-Sleep -Seconds 1
			if (Test-Path -LiteralPath $copiaLocal) {
				$copia = Get-Item -LiteralPath $copiaLocal
				if ($copia.Length -eq $archivo.Length) {
					$hashCopia = (Get-FileHash -LiteralPath $copiaLocal -Algorithm SHA256).Hash
					if ($hashCopia -eq $hashOrigen) {
						Write-Output 'APK COPIADO EXITOSAMENTE HACIA LA CARPETA InstalarApp DEL SAMSUNG (SHA256 verificado).'
						exit 0
					}
					break
				}
			}
		}
	}
	throw 'No se pudo confirmar la copia mediante SHA256 dentro del tiempo esperado. Compruebe el telefono y copie el APK manualmente si es necesario.'
}
catch {
	Write-Output ("No se pudo copiar o verificar el APK por MTP: " + $_.Exception.Message)
	exit 1
}
