# PonceanoFacturador (MAUI)

Aplicación MAUI para Android y Windows. Este proyecto se mantiene en `PonceanoFacturador\PonceanoFacturador.csproj`; la solución de Visual Studio es `PonceanoFacturador\PonceanoFacturador.slnx`.

## Requisitos y compilación

Instala el SDK .NET 11 y las cargas MAUI/Android apropiadas en Visual Studio. Desde la raíz del repositorio:

```powershell
dotnet restore PonceanoFacturador\PonceanoFacturador.csproj
dotnet build PonceanoFacturador\PonceanoFacturador.csproj -f net11.0-windows10.0.19041.0 -p:RuntimeIdentifier=win-x64
dotnet build PonceanoFacturador\PonceanoFacturador.csproj -f net11.0-android -t:Compile -p:RuntimeIdentifier=android-arm64
```

La aplicación usa el padrón RNC, los formatos DGII y los datos Tesseract incluidos en este directorio. No se deben quitar esos recursos aunque aumenten el tamaño del repositorio.

## Licencias

Consulta [INTEGRACION-LICENCIAS.md](INTEGRACION-LICENCIAS.md) para el flujo de validación local y remota y sus pruebas. La configuración Firebase del cliente contiene una clave de API pública de aplicación, no una credencial de Firebase Admin; dicha clave debe mantenerse restringida en Google Cloud/Firebase. Nunca agregues tokens de usuario ni credenciales de servicio.

Los perfiles locales de publicación Android y los almacenes de firma están excluidos de Git. Para una versión pública Android, configura la firma Release de forma segura fuera del repositorio y usa la clave compatible con el canal de distribución existente.
