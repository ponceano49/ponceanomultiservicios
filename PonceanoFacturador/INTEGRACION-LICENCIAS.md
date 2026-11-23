# Verificación de licencias local y remota

Al entrar, la aplicación comprueba primero la licencia o prueba local y la hora NTP. Después, con conexión y una sesión normal de Firebase, renueva el ID token y consulta `GET https://ponceanomultiservicios-production.up.railway.app/api/licencia/verificar` mediante `Authorization: Bearer`. El token no se escribe en registros y solo se envía al servidor fijo de Railway, sin redirecciones. La dirección editable de Ajustes es preparatoria y no cambia este destino.

La licencia local activa tiene prioridad. Una licencia remota `Activo` con vencimiento futuro también permite acceder y elimina los límites correspondientes durante la sesión, sin modificar la licencia ni la prueba locales. `Pendiente`, `Vencido` y los errores de red no revocan licencias locales. Si falla la consulta, se informa al usuario y se aplica la validación local; si la licencia local también impide el acceso, se mantiene el bloqueo o la activación de prueba correspondiente. El estado remoto se conserva únicamente en memoria y vence en la fecha informada; se vuelve a consultar al validar el inicio. No permite trabajar sin la validación NTP existente y no altera el modo administrador offline ni el acceso de prueba Debug.

## Prueba manual Android y Windows

1. Confirma que `https://ponceanomultiservicios-production.up.railway.app/health/ready` responde `ready`. Esto no prueba Firebase ni la licencia.
2. Usa una cuenta de Firebase con correo verificado, distinta de las cuentas con excepción de desarrollo. Inicia sesión en la aplicación; introduce la contraseña solo en la pantalla de la aplicación, nunca en el chat ni en registros.
3. Sin licencia remota activa, comprueba que una licencia o prueba local vigente sigue funcionando. Una cuenta nueva sin licencia puede solicitar la activación de prueba.
4. Para probar una licencia remota activa, un administrador autorizado debe registrar una licencia vigente para ese correo mediante el mecanismo administrativo del backend. Cierra sesión, vuelve a entrar y comprueba la pantalla de licencia y la creación de documentos sin límites LITE. No se deben borrar ni modificar los datos locales de prueba.
5. Prueba `Pendiente` o `Vencido` y una interrupción de la API: una licencia local vigente debe seguir funcionando; ante un error se muestra un aviso. Restablece el servicio y reintenta. No cambies las credenciales de producción para realizar la prueba.
6. Repite en Android y Windows y con sesión recordada. Cerrar sesión elimina el estado remoto de memoria. El webhook de PayPal todavía no procesa pagos; estas pruebas no verifican ni activan ese flujo.

Las pruebas automatizadas se ejecutan desde la raíz con `dotnet run --project Tests\LicenciaRemotaChecks\LicenciaRemotaChecks.csproj` y `dotnet run --project Tests\LicenciaSqliteChecks\LicenciaSqliteChecks.csproj`. La primera simula Firebase y Railway; la segunda prueba los límites en SQLite. Una prueba autenticada real requiere que un usuario autorizado inicie sesión en la aplicación; no se necesita compartir su contraseña ni su token.

## Seguridad antes de publicar

El cliente local no es un clon Git. No ejecutes `git init`, `git reset` ni `git add .` sobre esta carpeta para vincularla al repositorio existente. Primero hay que preparar un clon limpio de la rama correcta, seleccionar el código y los recursos necesarios, verificar su procedencia y revisar el contenido antes de incorporarlo. El archivo `.gitignore` reduce el riesgo de incluir archivos sensibles, pero no protege archivos ya rastreados ni detecta secretos incluidos dentro del código; revisa también los cambios preparados y los recursos antes de cualquier envío.

Para distribuir Android, firma el APK Release con el almacén de claves correspondiente a la instalación existente o a la clave de subida de Google Play, según el canal. No compartas ni subas el almacén o sus contraseñas. Un APK firmado con Android Debug es solo para pruebas. No desinstales una aplicación existente sin antes realizar y verificar una copia de seguridad de sus datos locales.
