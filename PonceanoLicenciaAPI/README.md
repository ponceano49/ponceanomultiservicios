# PonceanoLicenciaAPI

API independiente ASP.NET Core (.NET 10) para gestionar licencias de PonceanoFacturador. El checkout PayPal aún no está integrado en una pantalla MAUI; el dominio público configurado para Railway es `ponceanomultiservicios-production.up.railway.app`.

## Estado de despliegue

La API requiere PostgreSQL y valida Firebase ID tokens. En Railway configure `DATABASE_URL` (o `ConnectionStrings__Licencias`), `Firebase__ProjectId`, `AdminEmail` y un `AdminKey` aleatorio con al menos 32 bytes. No incluya secretos en Git ni en el APK. El arranque abre la base PostgreSQL, exige TLS con verificación del certificado y crea la tabla `licencias` si no existe; la API no inicia si falta la conexión o la base no está accesible. Confirme `/health/ready` y luego pruebe el endpoint protegido con un Firebase ID token válido antes de desplegar clientes.

El certificado `railway-postgres-root.crt` es la CA pública del servicio PostgreSQL de Railway y el Dockerfile la instala en el almacén de confianza para conservar `SslMode=VerifyFull`. Si Railway renueva o reemplaza la CA de PostgreSQL, actualice ese archivo con la nueva CA y redespliegue la API; no cambie a TLS sin validación del certificado.

En Development, solo si `Storage__EnableInMemory=true`, puede usarse el repositorio de memoria local. Se borra al reiniciar y no debe habilitarse en Railway.

El endpoint de administración exige un ID token Firebase válido, correo verificado coincidente con `AdminEmail` y `X-Admin-Key`. El endpoint de consulta también requiere un token Firebase válido con correo verificado; solo devuelve la licencia del correo del token, nunca de un correo arbitrario. La Web API key Firebase de la aplicación es un identificador público, no una credencial para validar tokens.

El checkout admite pagos únicos en USD: `mensual` cuesta 35 USD y añade 30 días; `anual` cuesta 360 USD y añade 365 días. `POST /api/pago/orden` requiere Firebase ID token con correo verificado. La API crea la orden con el precio fijo del servidor; guarda en PostgreSQL el usuario, plan y referencia aleatoria antes de devolver el enlace de aprobación. Tras la aprobación, `/api/pago/retorno` captura y verifica la orden directamente con PayPal. El webhook `PAYMENT.CAPTURE.COMPLETED` verifica firma, consulta la orden con la API de PayPal y concilia comercio, orden, referencia, captura, importe y moneda antes de activar la licencia.

Las órdenes y capturas se procesan transaccionalmente en PostgreSQL; el ID de captura y el ID de evento son únicos, por lo que un webhook repetido no prolonga de nuevo la licencia. Las compras sucesivas extienden desde la fecha de vencimiento existente si sigue vigente; si ya venció, el nuevo período comienza al procesarse el pago. Los pagos autenticados que no coincidan con una orden se reconocen para evitar reintentos infinitos, se registran como error sin guardar datos personales y **no activan** licencias; deberán revisarse manualmente.

PayPal permanece limitado a Sandbox salvo que se configure explícitamente `PayPal__AllowLivePayments=true`. No habilite esa variable durante pruebas. No se ha ejecutado una transacción real Sandbox ni conectado el checkout a una pantalla de la aplicación MAUI; integre la llamada autenticada al checkout antes de ofrecer pagos a los usuarios. Los límites de solicitudes actuales son por instancia y no reemplazan WAF/rate limiting distribuido. Restrinja el acceso administrativo, rote sus secretos y evite registrar tokens, cabeceras, datos de pago o correos en telemetría.

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
- `POST /api/pago/orden`: exige ID token Firebase con correo verificado. JSON: `{"plan":"mensual"}` o `{"plan":"anual"}`. Devuelve `ordenId`, precio fijado por el servidor y `urlAprobacion` de PayPal Sandbox. No acepta correo, importe, moneda ni duración del cliente.
- `GET /api/pago/retorno?token={ordenId}`: callback de PayPal tras la aprobación. Captura y consulta la orden con PayPal; activa únicamente si la captura está completada y coincide con la orden persistida.
- `GET /api/pago/cancelado`: callback informativo; no activa licencias.
- `POST /api/pago/webhook`: escucha `PAYMENT.CAPTURE.COMPLETED`; valida cabeceras PayPal `PAYPAL-AUTH-ALGO`, `PAYPAL-CERT-URL`, `PAYPAL-TRANSMISSION-ID`, `PAYPAL-TRANSMISSION-SIG`, `PAYPAL-TRANSMISSION-TIME`, consulta la orden directamente a PayPal y procesa captura idempotente. Configuración/indisponibilidad temporal devuelve 503; firma inválida, 401; JSON inválido, 400.

Ejemplo de consulta después de obtener un ID token recién emitido por Firebase (no utilice ni registre tokens de usuarios reales en consola):

```powershell
$headers = @{ Authorization = "Bearer $env:FIREBASE_ID_TOKEN" }
Invoke-RestMethod -Uri 'https://DOMINIO-PUBLICO-VERIFICADO/api/licencia/verificar' -Headers $headers
```

Para la administración se requiere tanto el token Firebase de la cuenta autorizada como `X-Admin-Key`. Configure `AdminEmail` y `AdminKey` en el panel de Variables de Railway; nunca en el cliente.

## Despliegue en Railway

1. Despliegue la carpeta del proyecto API mediante el repositorio/build context correcto. El servicio debe usar .NET 10; configure el comando de inicio si el builder no lo detecta automáticamente. Si Railway proporciona `PORT`, el backend escucha HTTP en `0.0.0.0` en ese puerto; Railway termina HTTPS en su proxy. Sin `PORT` se conserva la configuración ASP.NET Core habitual.
2. Añada PostgreSQL al proyecto Railway y vincule su `DATABASE_URL` al servicio API. Configure además `ASPNETCORE_ENVIRONMENT=Production`, `Firebase__ProjectId`, `AdminEmail` y `AdminKey` en Variables. Para Sandbox configure `PayPal__Environment=Sandbox`, `PayPal__ClientId`, `PayPal__ClientSecret`, `PayPal__WebhookId` y `PayPal__MerchantId` con valores de la app/cuenta Sandbox correctos. No configure `PayPal__AllowLivePayments=true`. La API crea las tablas de órdenes/eventos al iniciar y aborta si no puede abrir PostgreSQL con verificación TLS.
3. En PayPal Developer Dashboard, dentro de la aplicación REST Sandbox de la cuenta receptora, crea un webhook para `https://ponceanomultiservicios-production.up.railway.app/api/pago/webhook`, suscrito al evento `PAYMENT.CAPTURE.COMPLETED`. Copia el ID del webhook en `PayPal__WebhookId` en el mismo entorno Sandbox. Asegúrate de que `PayPal__MerchantId` corresponde a la cuenta receptora que cobra.
4. En el servicio API abra **Settings → Networking → Public Networking → Generate Domain**, seleccione el puerto interno correspondiente si Railway lo solicita y aplique/redeploy los cambios. La ausencia de `RAILWAY_PUBLIC_DOMAIN` en una consola no demuestra por sí sola el estado del dominio; compruébelo en la configuración del servicio y entorno correctos.
5. Con el dominio HTTPS asignado pruebe `GET /health/ready`. `Online` por sí solo no confirma la salud ni que las rutas estén disponibles. Verifique también los logs de inicio sin exponer secretos.
5. Pruebe `GET /api/licencia/verificar` con un Firebase ID token válido del proyecto y confirme que devuelve 200, no 401 ni 503. Pruebe tokens inválidos, expirados y correos no verificados; deben ser rechazados.
6. En Ajustes de MAUI se puede guardar manualmente el dominio raíz HTTPS en el dispositivo. Este campo es solo preparatorio: no conecta con la API ni altera la validación de licencia existente. No introduzca tokens o secretos. No conecte MAUI hasta confirmar el dominio, autenticar de forma segura y completar las pruebas.

## Pruebas Sandbox y pruebas automatizadas

Para probar de extremo a extremo, primero integre `POST /api/pago/orden` con una sesión Firebase verificada en el cliente. El usuario aprueba en PayPal Sandbox, regresa a `/api/pago/retorno`, y el backend captura y valida. Confirme el estado con `GET /api/licencia/verificar` usando un Firebase ID token válido. En PayPal Developer Dashboard use **Apps & Credentials → Sandbox**, una cuenta Sandbox de comprador y una cuenta Sandbox de vendedor; envíe luego una repetición del evento en **Webhooks Events** y confirme que la fecha de vencimiento no aumenta por segunda vez. No use credenciales Live.

Desde la raíz del repositorio ejecute `dotnet build PonceanoLicenciaAPI\PonceanoLicenciaAPI.csproj -c Release` y `dotnet run --project Tests\PayPalPaymentsChecks\PayPalPaymentsChecks.csproj -c Release`. Las pruebas simulan la API de PayPal y verifican planes, precio fijo, aprobación, conciliación de captura/comercio y que Live permanezca deshabilitado. No sustituyen prueba Sandbox real ni una prueba concurrente contra PostgreSQL.

Referencia oficial PayPal: https://developer.paypal.com/api/rest/webhooks/rest/.
