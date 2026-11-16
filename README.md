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
