namespace PonceanoFacturador.Services;

// RESERVADO PARA FUTURA API DGII: no autentica, almacena credenciales ni consulta el portal.
public sealed class ServicioAutenticacionDGII
{
    // FLUJO FUTURO (CUANDO SE TENGA LA API AUTORIZADA DGII):
    // 1. La API DGII solicita el número de código de la tarjeta.
    // 2. Un administrador autorizado accede al código correspondiente en almacenamiento local cifrado.
    // 3. La integración autorizada envía el código a la API oficial.
    // 4. La aplicación recibe la confirmación de inicio de sesión para la carga del 606 / 607.
    //
    // La estructura de credenciales y la interfaz de 40 códigos se implementarán únicamente
    // después de contar con autorización de administrador verificable y la especificación oficial.
    // No se guardarán contraseñas ni códigos en texto plano ni se devolverá un código vacío
    // ante una solicitud inválida o ausente.
}
