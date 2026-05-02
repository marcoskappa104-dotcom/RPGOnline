# 🖥️ TUTORIAL COMPLETO — SERVIDOR DEDICADO RPG ONLINE
## Unity 2022.3.62f3 + Mirror + Windows 11

---

# PARTE 1 — PREPARANDO O PROJETO NO UNITY

## 1.1 Configurar o RPGNetworkManager para modo servidor

Abra a **GameplayScene** e selecione o GameObject `NetworkManager`.
No componente `RPGNetworkManager`, configure:

| Campo | Valor |
|---|---|
| Network Address | IP do servidor (preenche depois) |
| Max Connections | 100 |
| Player Prefab | NetworkPlayerPrefab |
| Transport | KcpTransport |

No `KcpTransport`:
| Campo | Valor |
|---|---|
| Port | 7777 |

---

## 1.2 Configurar o NetworkGameplayBootstrapper

Este script decide se abre como HOST ou CLIENT.
Para o servidor dedicado, vamos usar uma **terceira opção: Server Only**.

Substitua o `NetworkGameplayBootstrapper.cs` por este:

```csharp
// NetworkGameplayBootstrapper.cs
// Detecta automaticamente se é servidor, host ou cliente
// via argumentos de linha de comando

using UnityEngine;
using Mirror;
using RPG.Managers;

namespace RPG.Network
{
    public class NetworkGameplayBootstrapper : MonoBehaviour
    {
        [Header("Conexão")]
        [SerializeField] public string serverAddress = "localhost";
        [SerializeField] public ushort serverPort    = 7777;

        private void Start()
        {
            // Servidor dedicado não precisa de personagem selecionado
            bool isServer = IsServerBuild();

            if (!isServer && GameManager.Instance?.SelectedCharacter == null)
            {
                UnityEngine.SceneManagement.SceneManager
                    .LoadScene(GameManager.SCENE_LOGIN);
                return;
            }

            // Configura porta
            var kcp = FindObjectOfType<kcp2k.KcpTransport>();
            if (kcp != null) kcp.Port = serverPort;

            if (isServer)
            {
                // MODO SERVIDOR DEDICADO — sem gráficos, sem player local
                Debug.Log($"[Network] Iniciando SERVIDOR DEDICADO na porta {serverPort}...");
                NetworkManager.singleton.StartServer();
            }
            else if (IsHostBuild())
            {
                // MODO HOST — servidor + cliente local (para testes)
                Debug.Log($"[Network] Iniciando como HOST na porta {serverPort}...");
                NetworkManager.singleton.StartHost();
            }
            else
            {
                // MODO CLIENTE — conecta ao servidor
                Debug.Log($"[Network] Conectando a {serverAddress}:{serverPort}...");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartClient();
            }
        }

        // Detecta -server nos argumentos de linha de comando
        private bool IsServerBuild()
        {
            // Build marcado como "Server Build" no Unity
            if (Application.isBatchMode) return true;

            // Argumento manual: MyGame.exe -server
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-server") return true;

            return false;
        }

        // Detecta -host nos argumentos
        private bool IsHostBuild()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-host") return true;
            return false;
        }
    }
}
```

---

## 1.3 Criar a cena do servidor

O servidor dedicado **não precisa de UI, câmera ou HUD**.
Mas pode usar a mesma GameplayScene — os elementos de UI simplesmente
não aparecem porque não há tela.

Recomendado: criar uma cena separada `ServerScene` mais leve.
Por enquanto, use a GameplayScene mesmo.

---

# PARTE 2 — FAZER O BUILD DO SERVIDOR

## 2.1 Configurar Build Settings

No Unity: **File → Build Settings**

### Para o SERVIDOR:
1. Certifique-se que a GameplayScene está na lista (index 2)
2. Marque a opção **"Server Build"** (caixa de seleção)
3. **Target Platform**: Windows (ou Linux para VPS)
4. **Architecture**: x86_64
5. Clique em **"Build"**
6. Escolha uma pasta, ex: `C:\RPGServer\`
7. Nome do executável: `RPGServer.exe`

### Para o CLIENTE (jogadores):
1. **Desmarque** "Server Build"
2. Build normalmente
3. Nome: `RPGGame.exe`

> ⚠️ São dois builds separados:
> - `RPGServer.exe` → roda no servidor (sem janela gráfica)
> - `RPGGame.exe`   → roda nos jogadores

---

# PARTE 3 — RODANDO O SERVIDOR

## 3.1 Rodando localmente (teste na sua máquina)

Abra o **Prompt de Comando** (CMD) e navegue até a pasta do servidor:

```batch
cd C:\RPGServer
RPGServer.exe -batchmode -nographics -logFile server.log
```

Explicação dos argumentos:
- `-batchmode` → sem interface gráfica (headless)
- `-nographics` → sem renderização (economiza GPU)
- `-logFile server.log` → salva os logs em arquivo

O servidor vai iniciar e ficar aguardando conexões na porta **7777 UDP**.

Você vai ver no arquivo `server.log`:
```
[Network] Iniciando SERVIDOR DEDICADO na porta 7777...
[Server] Servidor iniciado com sucesso.
```

---

## 3.2 Verificar se o servidor está rodando

Abra outro CMD:
```batch
netstat -ano | findstr :7777
```

Se aparecer uma linha com `0.0.0.0:7777` e `LISTENING`, o servidor está ativo.

---

## 3.3 Script .bat para iniciar o servidor facilmente

Crie um arquivo `StartServer.bat` na pasta do servidor:

```batch
@echo off
echo Iniciando servidor RPG...
echo Porta: 7777
echo.
RPGServer.exe -batchmode -nographics -logFile logs\server_%date:~-4,4%%date:~-7,2%%date:~0,2%.log
pause
```

Dê dois cliques no `.bat` para iniciar o servidor.

---

## 3.4 Ver os logs em tempo real

```batch
powershell Get-Content -Path "server.log" -Wait
```

Isso mostra os logs sendo escritos em tempo real, como um terminal.

---

# PARTE 4 — ABRINDO A PORTA NO FIREWALL (WINDOWS)

O servidor precisa ter a porta **7777 UDP** aberta no firewall.

## 4.1 Via interface gráfica

1. Abra: **Painel de Controle → Sistema e Segurança → Firewall do Windows**
2. Clique em **"Configurações avançadas"** (lado esquerdo)
3. Clique em **"Regras de Entrada"**
4. Clique em **"Nova Regra..."** (lado direito)
5. Selecione **"Porta"** → Próximo
6. Selecione **"UDP"** → Portas específicas: `7777` → Próximo
7. Selecione **"Permitir a conexão"** → Próximo
8. Marque todos (Domínio, Privado, Público) → Próximo
9. Nome: `RPG Server 7777` → Concluir

## 4.2 Via CMD (mais rápido, execute como Administrador)

```batch
netsh advfirewall firewall add rule name="RPG Server UDP 7777" protocol=UDP dir=in localport=7777 action=allow
netsh advfirewall firewall add rule name="RPG Server TCP 7777" protocol=TCP dir=in localport=7777 action=allow
```

---

# PARTE 5 — CONECTANDO JOGADORES

## 5.1 Conexão na mesma rede local (LAN)

### No servidor:
1. Descubra o IP local: abra CMD e digite `ipconfig`
2. Anote o **IPv4 Address**, exemplo: `192.168.1.100`

### No cliente (jogador):
No script `NetworkGameplayBootstrapper`, altere:
```csharp
[SerializeField] public string serverAddress = "192.168.1.100";
```

Ou melhor: **crie uma tela de conexão** para o jogador digitar o IP.

---

## 5.2 Conexão pela internet (WAN)

### No servidor:
1. Descubra o IP público: acesse https://meuip.com.br
2. Anote o IP público, exemplo: `177.50.123.45`

### No roteador (port forwarding):
1. Acesse o painel do roteador: `192.168.1.1` (geralmente)
2. Procure: **Port Forwarding / Encaminhamento de Portas / Virtual Server**
3. Adicione uma regra:
   - Porta externa: `7777`
   - Porta interna: `7777`
   - Protocolo: `UDP`
   - IP interno: `192.168.1.100` (IP da máquina do servidor)
4. Salve e reinicie o roteador

### No cliente (jogador):
```csharp
serverAddress = "177.50.123.45"; // IP público do servidor
```

---

## 5.3 Adicionar tela de conexão no jogo (UI)

Adicione esta tela entre o Login e o GameplayScene.

Crie um painel no Canvas da `CharacterScene` com:
- Campo de texto: **IP do Servidor**
- Campo de texto: **Porta** (opcional)
- Botão: **Conectar**

Script:

```csharp
// ConnectionPanel.cs
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using RPG.Network;
using RPG.Managers;

public class ConnectionPanel : MonoBehaviour
{
    [SerializeField] private TMP_InputField ipInput;
    [SerializeField] private TMP_InputField portInput;
    [SerializeField] private Button         connectButton;
    [SerializeField] private TMP_Text       statusText;

    private void Start()
    {
        // Carrega último IP usado
        ipInput.text   = PlayerPrefs.GetString("LastServerIP", "localhost");
        portInput.text = PlayerPrefs.GetString("LastServerPort", "7777");
        connectButton.onClick.AddListener(OnConnectClicked);
    }

    private void OnConnectClicked()
    {
        string ip   = ipInput.text.Trim();
        string port = portInput.text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            statusText.text = "Digite o IP do servidor!";
            return;
        }

        // Salva para próxima vez
        PlayerPrefs.SetString("LastServerIP", ip);
        PlayerPrefs.SetString("LastServerPort", port);
        PlayerPrefs.Save();

        // Guarda no GameManager para o Bootstrapper usar
        ConnectionConfig.ServerAddress = ip;
        ConnectionConfig.ServerPort    = ushort.TryParse(port, out ushort p) ? p : (ushort)7777;

        statusText.text = $"Conectando a {ip}:{port}...";
        GameManager.Instance.GoToGameplay();
    }
}

// Classe estática para passar configuração entre cenas
public static class ConnectionConfig
{
    public static string ServerAddress = "localhost";
    public static ushort ServerPort    = 7777;
}
```

E no `NetworkGameplayBootstrapper`, use:
```csharp
serverAddress = ConnectionConfig.ServerAddress;
serverPort    = ConnectionConfig.ServerPort;
```

---

# PARTE 6 — SERVIDOR NA NUVEM (OPCIONAL — RECOMENDADO)

Para um servidor online de verdade, use uma **VPS Linux**.
Opções baratas:
- **Oracle Cloud Free Tier** — GRÁTIS para sempre (1 OCPU, 1GB RAM)
- **Google Cloud** — US$0.01/hora (e2-micro grátis no free tier)
- **Contabo** — ~R$30/mês (4 vCPU, 8GB RAM)
- **Amazon Lightsail** — US$3.50/mês

## 6.1 Build para Linux

No Unity → File → Build Settings:
- **Target Platform**: Linux
- **Server Build**: ✅ marcado
- Arquitetura: x86_64
- Build → `RPGServer_Linux`

## 6.2 Enviar para o servidor Linux

```bash
# No seu Windows, use o WinSCP ou o SCP via terminal:
scp -r C:\RPGServer_Linux\ usuario@IP_DO_VPS:/home/usuario/rpgserver/
```

## 6.3 Rodar no Linux

```bash
# No VPS (conecte via SSH):
ssh usuario@IP_DO_VPS

# Dê permissão de execução:
chmod +x /home/usuario/rpgserver/RPGServer_Linux.x86_64

# Abra a porta no firewall Linux:
sudo ufw allow 7777/udp
sudo ufw allow 7777/tcp

# Rode em background (continua mesmo após fechar o SSH):
nohup /home/usuario/rpgserver/RPGServer_Linux.x86_64 \
  -batchmode -nographics \
  -logFile /home/usuario/rpgserver/logs/server.log &

echo "Servidor rodando!"
```

## 6.4 Script de inicialização automática (Linux)

Crie um serviço para o servidor reiniciar automaticamente:

```bash
sudo nano /etc/systemd/system/rpgserver.service
```

Cole isso:
```ini
[Unit]
Description=RPG Game Server
After=network.target

[Service]
Type=simple
User=usuario
WorkingDirectory=/home/usuario/rpgserver
ExecStart=/home/usuario/rpgserver/RPGServer_Linux.x86_64 -batchmode -nographics -logFile /home/usuario/rpgserver/logs/server.log
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable rpgserver   # inicia automaticamente no boot
sudo systemctl start rpgserver    # inicia agora
sudo systemctl status rpgserver   # verifica se está rodando
sudo journalctl -u rpgserver -f   # ver logs em tempo real
```

---

# PARTE 7 — TESTANDO TUDO

## 7.1 Teste 1 — Local (1 máquina)

```
Terminal 1: RPGServer.exe -batchmode -nographics
Terminal 2: RPGGame.exe   (IP: localhost)
Terminal 3: RPGGame.exe   (IP: localhost)  ← segunda instância
```

Os dois clientes devem se ver no mundo.

## 7.2 Teste 2 — LAN (2 máquinas na mesma rede)

```
Máquina A (servidor): RPGServer.exe -batchmode -nographics
Máquina B (cliente):  RPGGame.exe   (IP: 192.168.1.X do servidor)
```

## 7.3 Teste 3 — Internet

```
VPS Linux: RPGServer_Linux.x86_64 rodando
Qualquer máquina: RPGGame.exe  (IP público do VPS)
```

---

# RESUMO RÁPIDO

```
1. Faça 2 builds:
   □ Server Build (marcado)   → RPGServer.exe
   □ Server Build (desmarcado) → RPGGame.exe

2. Inicie o servidor:
   □ RPGServer.exe -batchmode -nographics

3. Abra a porta 7777 UDP no Firewall

4. Se internet: configure Port Forwarding no roteador

5. Clientes conectam pelo IP do servidor
```

