# PonceanoLicenciaAPI

API independiente ASP.NET Core (.NET 10) para gestionar licencias de PonceanoFacturador. La aplicación MAUI todavía no está conectada a esta API y no se confirmó el dominio público de Railway; no invente una URL de producción.

## Estado de despliegue

La API requiere PostgreSQL y valida Firebase ID tokens. En Railway configure `DATABASE_URL` (o `ConnectionStrings__Licencias`), `Firebase__ProjectId`, `AdminEmail` y un `AdminKey` aleatorio con al menos 32 bytes. No incluya secretos en Git ni en el APK. El arranque abre la base PostgreSQL, exige TLS con verificación del certificado y crea la tabla `licencias` si no existe; la API no inicia si falta la conexión o la base no está accesible. Confirme `/health/ready` y luego pruebe el endpoint protegido con un Firebase ID token válido antes de desplegar clientes.

El certificado `railway-postgres-root.crt` es la CA pública del servicio PostgreSQL de Railway y el Dockerfile la instala en el almacén de confianza para conservar `SslMode=VerifyFull`. Si Railway renueva o reemplaza la CA de PostgreSQL, actualice ese archivo con la nueva CA y redespliegue la API; no cambie a TLS sin validación del certificado.

En Development, solo si `Storage__EnableInMemory=true`, puede usarse el repositorio de memoria local. Se borra al reiniciar y no debe habilitarse en Railway.

El endpoint de administración exige un ID token Firebase válido, correo verificado coincidente con `AdminEmail` y `X-Admin-Key`. El endpoint de consulta también requiere un token Firebase válido con correo verificado; solo devuelve la licencia del correo del token, nunca de un correo arbitrario. La Web API key Firebase de la aplicación es un identificador público, no una credencial para validar tokens.

El webhook verifica la firma con PayPal mediante OAuth y **responde 503 incluso si la firma es válida**: aún no se procesa ni concilia el pago ni se activa automáticamente una licencia. PayPal puede reintentar la notificación. No habilite pagos hasta implementar almacenamiento duradero e idempotencia de eventos, validación de comercio/importe/moneda/estado y activación transaccional. Pruebe primero con Sandbox.

Los límites de solicitudes actuales son por instancia y no reemplazan WAF/rate limiting distribuido. Restrinja el acceso administrativo, rote sus secretos y evite registrar tokens, cabeceras, datos de pago o correos en telemetría.

## Ejecutar localmente

Desde esta carpeta, con SDK .NET 10 o superior, el modo local usa el almacén temporal y no necesita secretos:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Storage__EnableInMemory = 'true'
dotnet run --urls http://localhost:5000
```

No habilite esta opción fuera de desarrollo. Para ensayar PostgreSQL localmente configure `ConnectionStrings__Licencias` con una cadena segura por entorno.

### Contratos

- `GET /api/licencia/verificar?correo=cliente%40ejemplo.com` exige `Authorization: Bearer <Firebase ID token>` con audiencia/emisor del proyecto configurado y correo verificado. El parámetro correo es opcional; si se proporciona debe coincidir con el correo autenticado. Devuelve `EstadoLicencia` (`Activo`, `Pendiente`, `Vencido`) y `FechaVencimiento` (ISO 8601 UTC o `null`).
- `POST /api/licencia/activar-manual` exige también un ID token Firebase del administrador configurado en `AdminEmail` y `X-Admin-Key`. JSON: `correoUsuario`, `fechaVencimiento` (fecha y hora ISO 8601 con zona horaria explícita, futura) y `codigoTransaccionPago` (1–128 caracteres). Una repetición idéntica es idempotente; reutilizar el código con otros datos devuelve 409.
- `POST /api/pago/webhook`: JSON y cabeceras de firma PayPal `PAYPAL-AUTH-ALGO`, `PAYPAL-CERT-URL`, `PAYPAL-TRANSMISSION-ID`, `PAYPAL-TRANSMISSION-SIG`, `PAYPAL-TRANSMISSION-TIME`. Falta de configuración o indisponibilidad de PayPal devuelve 503; firma o cabeceras inválidas, 401; JSON inválido, 400.

Ejemplo de consulta después de obtener un ID token recién emitido por Firebase (no utilice ni registre tokens de usuarios reales en consola):

```powershell
$headers = @{ Authorization = "Bearer $env:FIREBASE_ID_TOKEN" }
Invoke-RestMethod -Uri 'https://DOMINIO-PUBLICO-VERIFICADO/api/licencia/verificar' -Headers $headers
```

Para la administración se requiere tanto el token Firebase de la cuenta autorizada como `X-Admin-Key`. Configure `AdminEmail` y `AdminKey` en el panel de Variables de Railway; nunca en el cliente.

## Despliegue en Railway

1. Despliegue la carpeta del proyecto API mediante el repositorio/build context correcto. El servicio debe usar .NET 10; configure el comando de inicio si el builder no lo detecta automáticamente. Si Railway proporciona `PORT`, el backend escucha HTTP en `0.0.0.0` en ese puerto; Railway termina HTTPS en su proxy. Sin `PORT` se conserva la configuración ASP.NET Core habitual.
2. Añada PostgreSQL al proyecto Railway y vincule su `DATABASE_URL` al servicio API. Configure además `ASPNETCORE_ENVIRONMENT=Production`, `Firebase__ProjectId`, `AdminEmail` y `AdminKey` en Variables. La API aborta el arranque si no puede abrir PostgreSQL con verificación TLS.
3. En el servicio API abra **Settings → Networking → Public Networking → Generate Domain**, seleccione el puerto interno correspondiente si Railway lo solicita y aplique/redeploy los cambios. La ausencia de `RAILWAY_PUBLIC_DOMAIN` en una consola no demuestra por sí sola el estado del dominio; compruébelo en la configuración del servicio y entorno correctos.
4. Con el dominio HTTPS asignado pruebe `GET /health/ready`. `Online` por sí solo no confirma la salud ni que las rutas estén disponibles. Verifique también los logs de inicio sin exponer secretos.
5. Pruebe `GET /api/licencia/verificar` con un Firebase ID token válido del proyecto y confirme que devuelve 200, no 401 ni 503. Pruebe tokens inválidos, expirados y correos no verificados; deben ser rechazados.
6. En Ajustes de MAUI se puede guardar manualmente el dominio raíz HTTPS en el dispositivo. Este campo es solo preparatorio: no conecta con la API ni altera la validación de licencia existente. No introduzca tokens o secretos. No conecte MAUI hasta confirmar el dominio, autenticar de forma segura y completar las pruebas.

## Pruebas locales

Desde esta carpeta ejecute `dotnet build -c Release` y luego `dotnet run --project ..\PonceanoLicenciaAPIChecks\PonceanoLicenciaAPIChecks.csproj -c Release`. La suite prueba almacenamiento en memoria, rutas protegidas y firmas PayPal simuladas; no sustituye integración con PostgreSQL, validación real de tokens Firebase ni prueba Sandbox.

Referencia oficial PayPal: https://developer.paypal.com/api/rest/webhooks/rest/.
