# RPG Online — Unity 2022.3.62f3
## Guia de Configuração Completo

---

## 📁 ESTRUTURA DE SCRIPTS

```
Assets/Scripts/
├── Data/
│   ├── CharacterStats.cs      ← Atributos, status derivados, fórmulas de dano
│   └── CharacterData.cs       ← Modelo de conta e personagem (serialização)
├── Managers/
│   ├── GameManager.cs         ← Singleton global (persiste entre cenas)
│   └── SaveManager.cs         ← Salvar/carregar contas e personagens em JSON
├── UI/
│   ├── LoginUIController.cs   ← Tela de Login e Criação de Conta
│   ├── CharacterUIController.cs ← Seleção e Criação de Personagens
│   ├── UIManager.cs           ← HUD da gameplay (HP, MP, target, skills)
│   ├── SkillSlotUI.cs         ← Slot da barra de skills com cooldown radial
│   ├── FloatingTextManager.cs ← Números de dano flutuantes (pool)
│   └── MonsterHealthBarUI.cs  ← Barra de HP acima dos mobs
├── Character/
│   ├── ITargetable.cs         ← Interface + base para entidades selecionáveis
│   ├── PlayerEntity.cs        ← Jogador: stats, dano, cura, regen, morte
│   └── MonsterEntity.cs       ← Mob: IA (Idle→Patrol→Chase→Combat), XP
├── Combat/
│   └── SkillSystem.cs         ← Skills, cooldown, walk-to-range, cast time
└── Systems/
    ├── PlayerController.cs    ← Input: LMB mover/selecionar, teclas de skill
    ├── CameraController.cs    ← RMB orbitar, Scroll zoom, segue player
    └── GameplayBootstrapper.cs ← Inicializa cena de gameplay
```

---

## 🚀 PASSO A PASSO DE CONFIGURAÇÃO

### 1. PACOTES NECESSÁRIOS (Package Manager)
- **TextMeshPro** (já incluído no Unity 2022)
- **AI Navigation** (NavMesh) — instale via Package Manager
- **Universal Render Pipeline (URP)** — recomendado (opcional)

### 2. CRIAR AS CENAS
No menu File → Build Settings, adicione:
```
Scenes/LoginScene      (index 0)
Scenes/CharacterScene  (index 1)  
Scenes/GameplayScene   (index 2)
```

---

## 🔐 CENA: LoginScene

### GameObject: --- MANAGERS ---
Crie um Empty GameObject chamado `Managers`:
- Adicione `GameManager.cs`
- Adicione `SaveManager.cs`
- Adicione `FloatingTextManager.cs`

### Canvas (UI → Canvas)
Configuração: Screen Space - Overlay, UI Scale Mode: Scale With Screen Size (1920x1080)

**Hierarquia dentro do Canvas:**
```
Canvas
├── LoginPanel
│   ├── Background (Image)
│   ├── Title (TMP_Text — "RPG Online")
│   ├── UsernameInput (TMP_InputField)
│   ├── PasswordInput (TMP_InputField — Content Type: Password)
│   ├── LoginButton (Button + TMP_Text "Entrar")
│   ├── CreateAccountButton (Button + TMP_Text "Criar Conta")
│   └── ErrorText (TMP_Text — cor vermelha)
└── CreateAccountPanel (desativado no início)
    ├── Background (Image)
    ├── Title (TMP_Text — "Criar Conta")
    ├── UsernameInput (TMP_InputField)
    ├── PasswordInput (TMP_InputField — Content Type: Password)
    ├── ConfirmPasswordInput (TMP_InputField — Content Type: Password)
    ├── SubmitButton (Button + TMP_Text "Criar")
    ├── BackButton (Button + TMP_Text "Voltar")
    ├── ErrorText (TMP_Text — cor vermelha)
    └── SuccessText (TMP_Text — cor verde)
```

### Empty GameObject: LoginController
- Adicione `LoginUIController.cs`
- Arraste todos os campos no Inspector

---

## 👤 CENA: CharacterScene

### Mesmo Managers do LoginScene (ou adicione novamente)

### Canvas
```
Canvas
├── SelectionPanel
│   ├── Title (TMP_Text — "Selecionar Personagem")
│   ├── ScrollView
│   │   └── Viewport → Content (Vertical Layout Group)
│   ├── CreateNewButton (Button — "Criar Novo")
│   └── LogoutButton (Button — "Sair")
└── CreationPanel (desativado)
    ├── Title (TMP_Text — "Criar Personagem")
    ├── NameInput (TMP_InputField)
    ├── RaceDropdown (TMP_Dropdown)
    ├── RaceInfoText (TMP_Text — descrição da raça)
    ├── CreateButton (Button — "Criar")
    ├── BackButton (Button — "Voltar")
    └── ErrorText (TMP_Text — cor vermelha)
```

### Prefab: CharacterSlot
- Crie um Button com filho TMP_Text
- Salve como prefab em Assets/Prefabs/

### Empty GameObject: CharacterController
- Adicione `CharacterUIController.cs`
- Configure todos os campos no Inspector
- Arraste o prefab CharacterSlot no campo correspondente
- Arraste o Content do ScrollView em `characterListContent`

---

## 🎮 CENA: GameplayScene

### Terreno
- Crie um Terrain (3D Object → Terrain) ou um Plane grande
- **IMPORTANTE**: Na Layer do Terrain, crie a layer "Terrain" e atribua ao objeto
- Configure NavMesh:
  - Window → AI → Navigation → Bake
  - Marque o Terrain como Navigation Static
  - Clique em Bake

### Layers necessárias (Edit → Project Settings → Tags & Layers)
```
Layer 6: Terrain
Layer 7: Targetable
```

### Prefab: Player
```
PlayerPrefab (Empty)
├── NavMeshAgent (componente)
├── PlayerEntity.cs
├── PlayerController.cs
│   ├── Terrain Layer: Terrain
│   └── Targetable Layer: Targetable
├── SkillSystem.cs
│   └── Skills: configure Q,W,E,R
└── Modelo 3D (filho)
    └── Animator (com trigger "Attack")
```

### Câmera Principal
- Adicione `CameraController.cs` à Main Camera
- Configurações iniciais: Yaw=45, Pitch=50, Distance=12

### Prefab: Monster
```
MonsterPrefab
├── NavMeshAgent
├── MonsterEntity.cs
│   ├── Level, HP, ATK, DEF
│   ├── AggroRange, AttackRange
│   └── PatrolPoints (array de Transforms)
├── Collider (CapsuleCollider — Layer: Targetable)
├── SelectionIndicator (círculo no chão — filho)
└── WorldSpaceCanvas (filho)
    └── MonsterHealthBarUI.cs
        └── Slider (HP Bar)
```

### Canvas HUD
```
Canvas (Screen Space - Overlay)
├── PlayerFrame
│   ├── PlayerNameText (TMP)
│   ├── LevelText (TMP)
│   ├── HPBar (Slider)
│   │   └── HPText (TMP)
│   └── MPBar (Slider)
│       └── MPText (TMP)
├── TargetPanel
│   ├── TargetNameText (TMP)
│   └── TargetHPBar (Slider)
│       └── TargetHPText (TMP)
├── SkillBar
│   ├── SkillSlot_Q (SkillSlotUI.cs)
│   │   ├── IconImage (Image)
│   │   ├── CooldownOverlay (Image — Fill Radial360)
│   │   ├── CooldownText (TMP)
│   │   └── HotkeyText (TMP — "Q")
│   ├── SkillSlot_W
│   ├── SkillSlot_E
│   └── SkillSlot_R
├── ExpBar (Slider)
│   └── ExpText (TMP)
└── MessageText (TMP — centro da tela)
```

### Empty GameObjects na cena
```
Managers
├── GameManager.cs     (se não persistir do LoginScene)
├── SaveManager.cs
└── FloatingTextManager.cs
    └── floatingTextPrefab: prefab com TMP_Text

Bootstrapper
└── GameplayBootstrapper.cs
    ├── playerPrefab: PlayerPrefab
    ├── spawnPoint: Transform do ponto de spawn
    └── cameraController: Main Camera

UIController
└── UIManager.cs
    └── (todos os campos do HUD)
```

---

## 🎯 CONFIGURAÇÃO DE SKILLS

No prefab do Player, no componente `SkillSystem`, adicione skills:

```
Skill 0 (Q): Golpe Básico
  Name: Golpe
  Type: Physical
  Target: Enemy
  Cooldown: 2
  ManaCost: 5
  Range: 2.5
  AtkMultiplier: 1.0
  CastTime: 0
  AnimTrigger: "Attack"

Skill 1 (W): Golpe Pesado
  Name: Golpe Pesado
  Type: Physical
  Cooldown: 8
  ManaCost: 15
  Range: 2
  AtkMultiplier: 2.0

Skill 2 (E): Bola de Fogo
  Name: Bola de Fogo
  Type: Magical
  Cooldown: 5
  ManaCost: 20
  Range: 8
  AtkMultiplier: 1.5

Skill 3 (R): Cura
  Name: Cura
  Type: Heal
  Target: Self
  Cooldown: 15
  ManaCost: 30
  Range: 0
  AtkMultiplier: 1.0
```

---

## 💡 DICAS IMPORTANTES

### FloatingText Prefab
Crie um prefab simples:
- Empty GameObject
- Filho: TMP_Text (tamanho ~24, Bold, com outline)
- NÃO precisa de Canvas — o FloatingTextManager posiciona em World Space

### NavMesh
- Todo terreno onde o player anda precisa ter NavMesh baked
- NavMeshAgent nos monstros com mesmo Surface

### Saves
Os saves ficam em: `Application.persistentDataPath/accounts/`
Windows: `C:/Users/[user]/AppData/LocalLow/[CompanyName]/[ProductName]/accounts/`

---

## 🔄 FLUXO DO JOGO

```
LoginScene
  ↓ Login bem-sucedido → GameManager.SetAccount()
CharacterScene
  ↓ Personagem selecionado → GameManager.SetSelectedCharacter()
GameplayScene
  ↓ GameplayBootstrapper spawna player com os dados
  ↓ PlayerEntity.Initialize(charData) carrega HP, MP, stats
  ↓ PlayerController recebe input do mouse
  ↓ CameraController segue o player
  ↓ SkillSystem gerencia as skills
  ↓ UIManager atualiza o HUD
  ↓ SaveManager.SaveCharacter() em OnApplicationQuit
```

---

## 📝 PRÓXIMOS PASSOS (versões futuras)

- [ ] Inventário e sistema de equipamentos
- [ ] NPCs com diálogo
- [ ] Sistema de quests
- [ ] Multiplayer com Mirror/FishNet
- [ ] Loja e economia
- [ ] Mapa e múltiplas zonas
- [ ] Sistema de grupo (party)
- [ ] Chat global e por canal
