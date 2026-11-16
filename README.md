# WiFi Transfer · Windows 11

Explorador visual **FTP/FTPS** para navegar en un Xiaomi desde Windows 11, con miniaturas progresivas y transferencias bidireccionales. Solo requiere que el teléfono ejecute un servidor FTP compatible; **no se instala una APK nueva**.

## Descargar el ejecutable

1. Abre [GitHub Actions](https://github.com/javierpuertomontt/wifi-transfer/actions).
2. Selecciona **Compilar WiFi Transfer (.exe)** y abre la ejecución completada correctamente (verde).
3. Baja el artefacto **WiFiTransfer-Windows-x64** y extrae **WiFiTransfer.exe**.
4. Ejecuta el EXE en Windows 11 x64; no requiere instalar .NET.

GitHub puede pedir que inicies sesión para descargar artefactos. Un ejecutable sin firma comercial puede disparar una advertencia de Windows SmartScreen.

## Conectarse al Xiaomi 14T Pro

1. Conecta Xiaomi y notebook a la misma WiFi y abre WiFi Pro Servidor FTP en Android.
2. Inicia el servidor en el teléfono y copia su **IP**, **puerto**, **usuario** y **contraseña**.
3. Escríbelos en WiFi Transfer, marca **FTPS** *solo* si tu servidor soporta FTPS explícito y dispone de un certificado TLS confiable.
4. Pulsa **Conectar**. Navega por carpetas con doble clic.
5. **Descargar**: selecciona en Xiaomi los archivos o carpetas (Ctrl / Shift) y elige **Carpeta destino…**.
6. **Subir archivos**: desde el Xiaomi abre el diálogo para seleccionar archivos del PC. Alternativamente, entra a **Este equipo**, selecciona archivos o carpetas y pulsa **Subir archivos** (destino: la última carpeta abierta del Xiaomi).

El programa recuerda host, puerto y usuario en `%LOCALAPPDATA%\WiFiTransfer\connection.json`, pero **nunca guarda contraseñas**.

## Diseño y rendimiento

- Aplicación WPF/.NET 10, compilada autónoma en Windows x64.
- Seis modos de visualización: iconos muy grandes, grandes, medianos, pequeños, lista y detalles.
- Cuadrícula de filas virtualizadas; búsqueda local en el listado de la carpeta abierta.
- Miniaturas JPG, JPEG, PNG, BMP, GIF y TIFF, generadas bajo demanda y guardadas en caché en `%LOCALAPPDATA%\WiFiTransfer\thumbnails`.
- Se descargan imágenes completas cuando es necesario crear su miniatura. La primera navegación por miles de fotos puede requerir tiempo: FTP no entrega miniaturas nativas.
- El límite inicial de imágenes descargadas para miniaturas remotas es 25 MB por archivo. Otros tipos (incluyendo HEIC, PDF y videos) se muestran con iconos y se pueden transferir.
- Una conexión de exploración, una de miniaturas y hasta dos transferencias simultáneas.
- Las descargas usan `.wft.part`, reanudación si el servidor lo permite y comprobación de tamaño cuando FTP lo proporciona.
- Las subidas usan sufijo temporal `.wft-upload` y cambio de nombre al terminar. No se sobrescriben archivos existentes.
- Solo funciona en FTP local; no activa puertos ni publica archivos hacia internet.

## Limitaciones y seguridad

FTP convencional **no cifra** contraseñas ni datos. FTPS exige un servidor compatible y certificado validable. Úsalo únicamente en una red doméstica de confianza cuando no puedas activar FTPS. El servidor Android puede restringir conexiones concurrentes, desactivar reanudaciones o limitar la lectura de carpetas por permisos de Android. No es posible comprobar desde GitHub Actions el funcionamiento en tu teléfono.

No hay miniaturas garantizadas para HEIC, PDF ni videos en esta versión. Las operaciones de borrado y sobrescritura están deshabilitadas deliberadamente. La caché no mantiene originales ni credenciales; el tamaño se recorta al inicio de la aplicación a un máximo aproximado de 1 GB.

## Compilar desde código fuente

En Windows 11 con SDK de .NET 10:

```powershell
dotnet restore src/WiFiTransfer/WiFiTransfer.csproj
dotnet publish src/WiFiTransfer/WiFiTransfer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

El proyecto se compila automáticamente con el único workflow `.github/workflows/build-windows.yml` en cada push a `main` o manualmente mediante **Run workflow**.


## Novedades: selección, meses, precarga y arrastrar/soltar

- **Casillas de selección**: cada archivo presenta una casilla arriba a la izquierda. Un clic marca o desmarca ese archivo sin modificar las otras selecciones. Con **Shift + clic en una casilla** se selecciona el intervalo desde el último archivo marcado; también puedes seleccionar todos los elementos visibles o limpiar la selección.
- **Agrupar por mes**: habilitado inicialmente. La carpeta se divide visualmente en secciones «Noviembre 2026», «Octubre 2026», etc. Los selectores **Año** y **Mes** permiten filtrar (por fecha de modificación que anuncia el servidor FTP; no por EXIF). Los filtros no mueven físicamente los archivos ni crean nuevas carpetas.
- **Cargar todas las miniaturas**: recorre automáticamente las imágenes compatibles del filtro actual, las almacena en caché y muestra un contador. **No realiza scroll**. Se puede cancelar y la caché ya generada permanece disponible. Las imágenes remotas de más de 25 MB o formatos no compatibles se omiten. FTP puede necesitar transferir el contenido de cada fotografía, de modo que la primera precarga puede tardar bastante.
- **Arrastrar hacia WiFi Transfer**: suelta archivos o carpetas del Explorador de Windows sobre el panel central de WiFi Transfer. En modo Xiaomi se suben a la carpeta remota actualmente abierta. En modo Este equipo se copian a la carpeta local actual sin sobrescribir.
- **Arrastrar desde WiFi Transfer hacia Windows**: arrastra archivos seleccionados hacia una carpeta del Explorador de Windows. En modo local es inmediato. En modo Xiaomi es necesario **preparar una descarga temporal**: la primera vez puede tardar y, si se suelta el ratón antes de terminar, debes arrastrar de nuevo (ya estará preparado). No es posible arrastrar un archivo FTP aún no descargado con la misma inmediatez que un archivo local.
- Los archivos temporales de arrastre quedan en `%LOCALAPPDATA%\\WiFiTransfer\\drag-cache` y se eliminan en futuros inicios cuando tienen más de dos días de antigüedad; se evita borrarlos inmediatamente para permitir que Windows termine la copia.
- Como antes, no se sobrescriben archivos existentes sin intervención explícita. **No se ha modificado la configuración de conexión FTP.**
