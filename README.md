# DualSenseBT-Triggers

Utilitário residente em segundo plano (System Tray daemon) em **C# (.NET 8)** para **Windows 10/11 (x64)** que ativa e gerencia automaticamente os gatilhos adaptáveis (**L2** e **R2**) do controle **Sony DualSense** / **DualSense Edge** conectado via **Bluetooth**, alternando perfis de resistência conforme o jogo em foco.

## Recursos

- **Zero Polling (100% orientado a eventos Win32):**
  - Detecção instantânea de conexão/desconexão HID via `RegisterDeviceNotification` (`WM_DEVICECHANGE` / `GUID_DEVINTERFACE_HID`).
  - Detecção de janela/jogo em foco via `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`) + `QueryFullProcessImageName` (`PROCESS_QUERY_LIMITED_INFORMATION`).
- **Comunicação HID não invasiva:**
  - Abre o DualSense com `FILE_SHARE_READ | FILE_SHARE_WRITE`, sem bloquear Windows, Steam Input ou jogos.
  - Identifica exclusivamente conexões Bluetooth (`OutputReportByteLength == 78`, VID `0x054C`, PID `0x0CE6` / `0x0DF2`).
  - Envia pacotes `Report ID 0x31` (78 bytes) com `Valid Flag 0 = 0x0C` (somente L2/R2) e `Valid Flag 1 = 0x00` (sem interferir em vibração ou LEDs/Lightbar), assinados com CRC-32 (`0xEDB88320`, semente `0xA2`, Little-Endian).
- **System Tray:**
  - Ícone dinâmico gerado via `System.Drawing` (sem `.ico` externo).
  - Status do controle e perfil ativo, Pausar/Retomar, Recarregar/Abrir `profiles.json`, Iniciar com o Windows (`HKCU\...\Run`) e reset automático (`off`) ao sair.

## Compilação Cruzada no Arch Linux (para Windows x64)

Instale o SDK do .NET 8 (caso ainda não tenha):

```bash
sudo pacman -S dotnet-sdk-8.0
```

Gere o executável único e autônomo (`win-x64`):

```bash
dotnet publish DualSenseBT-Triggers.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o ./publish/win-x64
```

O executável final será gerado em `./publish/win-x64/DualSenseBT-Triggers.exe`.

## Configuração (`profiles.json`)

Na primeira execução no Windows, o arquivo `profiles.json` é criado automaticamente ao lado do `.exe`.

### Modos de gatilho suportados

| Modo | Código | Parâmetros (`params`) |
| :--- | :---: | :--- |
| `off` | `0x00` | `[0, 0, 0]` (gatilho livre) |
| `continuous` | `0x01` | `[start_pos (0-255), force (0-255), 0]` |
| `weapon` | `0x02` | `[start_pos (0-255), end_pos (0-255), force (0-255)]` |
| `vibration` | `0x06` | `[frequency_hz (1-255), amplitude (0-255), start_pos (0-255)]` |
