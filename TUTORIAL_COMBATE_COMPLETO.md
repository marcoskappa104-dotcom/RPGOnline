# ⚔️ TUTORIAL COMPLETO — SISTEMA DE COMBATE
## Unity 2022.3.62f3 — RPG Online

---

# PARTE 1 — ENTENDENDO O SISTEMA DE DANO

Antes de montar qualquer coisa no Unity, entenda como os cálculos funcionam.

## 1.1 Atributos Base (CharacterStats.cs)

Todo personagem tem 6 atributos que você configura:

| Atributo | Sigla | Função principal |
|---|---|---|
| Força | STR | Dano físico, HP |
| Agilidade | AGI | Velocidade, Esquiva |
| Vitalidade | VIT | HP, Defesa |
| Destreza | DEX | Precisão, Cast Speed |
| Inteligência | INT | Magia, MP |
| Sorte | LUK | Crítico, Drop |

## 1.2 Como o dano é calculado

### Dano Físico:
```
ATK  = (STR × 2) + (DEX × 1) + Level
DEF  = (VIT × 2) + (STR × 0.5)

Dano = ATK × (1 - DEF / (DEF + 100))
```

**Exemplo prático:**
- Player com STR=20, DEX=10, Level=5
- ATK = (20×2) + (10×1) + 5 = **55**
- Mob com DEF=10
- Dano = 55 × (1 - 10/110) = 55 × 0.909 = **~50**

### Dano Mágico:
```
MATK = (INT × 2.5) + (DEX × 0.5) + Level
MDEF = (INT × 2) + (VIT × 1)

DanoMágico = MATK × (1 - MDEF / (MDEF + 100))
```

### Dano Crítico:
```
Chance de Crítico = LUK × 0.3  (em %)
Dano Crítico      = Dano normal × 1.5
```

**Exemplo:** 30 LUK = 9% de chance de crítico

### Skills multiplicam o dano:
```
Dano Final da Skill = ATK × AtkMultiplier
```
- Skill com `AtkMultiplier = 1.0` → dano normal
- Skill com `AtkMultiplier = 2.0` → dano dobrado
- Skill com `AtkMultiplier = 0.5` → dano pela metade (DoT/multi-hit)

---

# PARTE 2 — MONTANDO UM MONSTRO

## 2.1 Criar o GameObject do Monstro

**No Unity, na GameplayScene:**

1. Hierarquia → clique direito → **Create Empty**
2. Renomeie para `Slime` (ou o nome do mob)
3. Com o `Slime` selecionado, adicione os componentes:

```
Slime (GameObject)
├── NavMeshAgent          ← Unity Add Component → Navigation → NavMesh Agent
├── CapsuleCollider       ← Unity Add Component → Physics → Capsule Collider
└── MonsterEntity.cs      ← nosso script
```

## 2.2 Configurar o NavMeshAgent

Selecione o `Slime` → encontre o componente **NavMeshAgent**:

| Campo | Valor recomendado |
|---|---|
| Speed | 3.5 |
| Angular Speed | 360 |
| Acceleration | 8 |
| Stopping Distance | 0 (o script controla) |
| Radius | 0.5 |
| Height | 1.0 |

## 2.3 Configurar o CapsuleCollider

| Campo | Valor |
|---|---|
| **Layer** | Targetable ← MUITO IMPORTANTE |
| Is Trigger | ❌ desmarcado |
| Radius | 0.5 |
| Height | 1.8 |
| Direction | Y-Axis |

> ⚠️ **OBRIGATÓRIO**: A layer do collider DEVE ser **Targetable**.
> Sem isso o jogador não consegue clicar e selecionar o mob.
> 
> Como criar a layer: Edit → Project Settings → Tags and Layers
> Layer 7: Targetable

## 2.4 Configurar o MonsterEntity

Com o `Slime` selecionado, encontre o componente **MonsterEntity**:

### Seção "Identidade":
| Campo | Exemplo Slime | Exemplo Boss |
|---|---|---|
| Monster Name | Slime | Dragão Ancião |
| Level | 1 | 50 |

### Seção "Stats":
| Campo | Slime Fraco | Mob Médio | Boss |
|---|---|---|---|
| Base HP | 150 | 500 | 5000 |
| Base ATK | 15 | 45 | 200 |
| Base DEF | 5 | 20 | 80 |

### Seção "Comportamento":
| Campo | Descrição | Slime | Mob Agressivo |
|---|---|---|---|
| Aggro Range | Raio que detecta o player | 6 | 12 |
| Attack Range | Distância do ataque | 2 | 3 |
| Kite Distance | Distância MÍNIMA do player | 1.5 | 2 |
| Attack Cooldown | Segundos entre ataques | 2.5 | 1.5 |

### Seção "Patrulha" (opcional):
- Crie GameObjects vazios na cena chamados `PatrolPoint_1`, `PatrolPoint_2`, etc.
- Posicione-os pelo terreno
- Arraste-os para o array **Patrol Points** do MonsterEntity

### Seção "Recompensa":
| Campo | Slime | Boss |
|---|---|---|
| Exp Reward | 30 | 5000 |

## 2.5 Adicionar Visual ao Monstro

Por enquanto use primitivos do Unity:

1. Clique direito no `Slime` → **3D Object → Capsule**
2. Renomeie o filho para `Visual`
3. Mude a cor:
   - Crie um Material: Assets → clique direito → Create → Material
   - Nome: `SlimeMaterial`
   - Cor: Verde (#00AA00)
   - Arraste o material para a Capsule

## 2.6 Adicionar Barra de HP acima do Monstro

1. Clique direito no `Slime` → **UI → Canvas**
2. No componente **Canvas**:
   - Render Mode: **World Space**
   - Width: 1.5
   - Height: 0.2
   - Scale: 0.01, 0.01, 0.01
   - Position Y: 1.2 (acima da cabeça)

3. Dentro do Canvas, clique direito → **UI → Slider**
4. Configure o Slider:
   - Min Value: 0
   - Max Value: 1
   - Value: 1
   - Direction: Left to Right
   - Remova o Handle (delete o filho Handle Slide Area)
   - Background: cor vermelha escura (#660000)
   - Fill: cor verde (#00CC00)

5. Adicione o componente **MonsterHealthBarUI** ao Canvas
6. Arraste o Slider para o campo **HP Slider** no MonsterHealthBarUI

## 2.7 Adicionar Indicador de Seleção

1. Clique direito no `Slime` → **3D Object → Cylinder**
2. Renomeie para `SelectionIndicator`
3. Configure o Transform:
   - Position Y: 0.02
   - Scale: 1, 0.01, 1
4. Crie um Material verde brilhante e aplique
5. Arraste `SelectionIndicator` para o campo **Selection Indicator** no MonsterEntity
6. O script vai ativar/desativar automaticamente ao selecionar

## 2.8 Salvar como Prefab

1. Arraste o `Slime` da Hierarchy para a pasta `Assets/Prefabs/`
2. Confirme "Original Prefab"
3. Agora você pode arrastar o prefab para a cena quantas vezes quiser

---

# PARTE 3 — MONTANDO AS SKILLS

## 3.1 Como as Skills Funcionam

```
Jogador pressiona Q
        ↓
SkillSystem.TryUseSkill(0)
        ↓
Tem alvo? → Não: mostra "Selecione um alvo!"
        ↓
Cooldown ativo? → Sim: mostra "X.Xs"
        ↓
MP suficiente? → Não: mostra "MP insuficiente!"
        ↓
Dentro do range?
  Não → anda automaticamente até o range
  Sim → executa direto
        ↓
SpendMP(manaCost)
Inicia cooldown
Toca animação
Calcula dano: ATK × AtkMultiplier
TakeDamage no alvo
```

## 3.2 Adicionar Skills ao Player Prefab

Selecione o **NetworkPlayerPrefab** (ou seu prefab de player).
Encontre o componente **SkillSystem**.

Clique no **+** em "Skills" para adicionar cada skill:

### Skill 0 — Q: Golpe Básico
```
Name:          Golpe
Type:          Physical
Target:        Enemy
Cooldown:      1.5
Mana Cost:     5
Range:         2.5
Atk Multiplier: 1.0
Cast Time:     0
Anim Trigger:  Attack
```

### Skill 1 — W: Golpe Pesado
```
Name:          Golpe Pesado
Type:          Physical
Target:        Enemy
Cooldown:      8
Mana Cost:     15
Range:         2
Atk Multiplier: 2.2
Cast Time:     0
Anim Trigger:  HeavyAttack
```

### Skill 2 — E: Bola de Fogo
```
Name:          Bola de Fogo
Type:          Magical
Target:        Enemy
Cooldown:      5
Mana Cost:     20
Range:         8
Atk Multiplier: 1.8
Cast Time:     1.0
Anim Trigger:  Cast
```

### Skill 3 — R: Cura
```
Name:          Cura
Type:          Heal
Target:        Self
Cooldown:      15
Mana Cost:     30
Range:         0
Atk Multiplier: 1.0
Cast Time:     0.5
Anim Trigger:  Heal
```

## 3.3 Balanceamento de Skills por Raça

Use multiplicadores diferentes dependendo da build:

| Raça | Melhor uso | Skills recomendadas |
|---|---|---|
| Humano | Qualquer | Mix físico + mágico |
| Elfo | Magia | Skills Mágicas (INT alto) |
| Anão | Físico tanque | Golpes físicos (STR+VIT) |
| Orc | Físico DPS | Golpes pesados (STR) |
| Morto-Vivo | Magia sombria | Skills Mágicas (INT) |

---

# PARTE 4 — MONTANDO A BARRA DE SKILLS (HUD)

## 4.1 Criar os Skill Slots no Canvas

No Canvas da GameplayScene, crie:

```
Canvas
└── SkillBar (Panel)
    ├── SkillSlot_Q
    ├── SkillSlot_W
    ├── SkillSlot_E
    └── SkillSlot_R
```

## 4.2 Configurar cada Skill Slot

Cada slot é um **Button** com esta hierarquia:

```
SkillSlot_Q (Button)
├── Background (Image)          ← fundo do slot (cinza escuro)
├── IconImage (Image)           ← ícone da skill
├── CooldownOverlay (Image)     ← escurecimento de cooldown
├── CooldownText (TMP_Text)     ← "2.3" segundos restantes
└── HotkeyText (TMP_Text)       ← "Q"
```

### Configurar o CooldownOverlay:
- Image Type: **Filled**
- Fill Method: **Radial 360**
- Fill Origin: **Top**
- Color: preto com 70% de transparência (0, 0, 0, 180)
- Fill Amount: 0 (começa vazio)

### Adicionar o SkillSlotUI.cs:
1. Selecione `SkillSlot_Q`
2. Add Component → **SkillSlotUI**
3. Arraste os campos:
   - Icon Image → `IconImage`
   - Cooldown Overlay → `CooldownOverlay`
   - Cooldown Text → `CooldownText`
   - Hotkey Text → `HotkeyText`

## 4.3 Conectar os Slots ao UIManager

Selecione o **UIManager** na cena.
No campo **Skill Slots** (array de tamanho 4):
- Slot 0 → `SkillSlot_Q`
- Slot 1 → `SkillSlot_W`
- Slot 2 → `SkillSlot_E`
- Slot 3 → `SkillSlot_R`

## 4.4 Adicionar Ícones às Skills (opcional por enquanto)

Se não tiver ícones ainda:
1. Crie texturas simples 64×64px coloridas
2. Importe para Assets/Icons/
3. Texture Type: **Sprite (2D and UI)**
4. Arraste para o campo **Icon** de cada SkillData no SkillSystem
5. O SkillSlotUI mostrará o ícone automaticamente

---

# PARTE 5 — CONFIGURANDO OS LAYERS (OBRIGATÓRIO)

Sem os layers corretos, o clique no mob não funciona.

## 5.1 Criar os Layers

**Edit → Project Settings → Tags and Layers**

```
Layer 6:  Terrain
Layer 7:  Targetable
```

## 5.2 Aplicar os Layers

### Terrain/Chão:
- Selecione o Terrain ou Plane
- No topo do Inspector: **Layer → Terrain**

### Monstros (todos):
- Selecione o CapsuleCollider do mob (não o GameObject pai!)
- **Layer → Targetable**
- Quando perguntar "Change children?" → **Yes, change children**

## 5.3 Configurar o PlayerController

Selecione o player prefab → componente **PlayerController**:

| Campo | Valor |
|---|---|
| Terrain Layer | Terrain (layer 6) |
| Targetable Layer | Targetable (layer 7) |

---

# PARTE 6 — SISTEMA DE COMBATE COMPLETO PASSO A PASSO

## 6.1 Fluxo Visual do Combate

```
┌─────────────────────────────────────────────────┐
│  1. PLAYER CLICA NO MOB                         │
│     → Anel verde aparece embaixo do mob          │
│     → Painel de alvo aparece no HUD             │
│     → HP do alvo visível                         │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│  2. PLAYER PRESSIONA Q (skill 0)                │
│     → Verifica cooldown, MP, alvo               │
│     → Se fora de range: anda automaticamente    │
│     → Se dentro do range: usa imediatamente     │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│  3. CHEGOU NO RANGE / DENTRO DO RANGE           │
│     → Para o movimento                          │
│     → Vira para o mob                           │
│     → Toca animação "Attack"                    │
│     → Calcula: ATK × Multiplier                 │
│     → Aplica redução de DEF                     │
│     → Verifica Crítico (LUK × 0.3%)             │
│     → Número de dano aparece flutuando          │
│     → HP do mob baixa                           │
│     → Cooldown do slot Q começa (overlay radial)│
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│  4. MOB REAGE                                   │
│     → Detecta o player (aggroRange)             │
│     → Corre em direção ao player                │
│     → Para a attackRange do player              │
│     → NÃO GRUDA (mantém kiteDistance)           │
│     → Ataca com cooldown próprio                │
│     → Número vermelho aparece no player         │
│     → HP do player baixa no HUD                 │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│  5. PLAYER PRESSIONA Q NOVAMENTE                │
│     → Se cooldown: mostra tempo restante        │
│     → Se cooldown zerou: usa de novo            │
│     → Repete até mob morrer                     │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│  6. MOB MORRE                                   │
│     → Animação de morte (se tiver)              │
│     → "+X XP" aparece flutuando                 │
│     → XP adicionado ao personagem              │
│     → Se level up: "LEVEL UP!" aparece          │
│     → Dados salvos em JSON                      │
│     → Mob destruído após 3 segundos             │
└─────────────────────────────────────────────────┘
```

## 6.2 Cancelar Ação

O jogador pode cancelar a ação pendente a qualquer momento:
- **Clique esquerdo no terreno** → para de andar para o mob, cancela skill pendente

## 6.3 Trocar de Alvo

- **Clique no mob B** enquanto andando para mob A → seleciona B, cancela ação de A
- A nova skill pressionada após selecionar B vai andar até B

---

# PARTE 7 — EXEMPLO PRÁTICO COMPLETO

## Criando o "Slime Verde" do zero

### Passo 1 — Criar o GameObject
```
Hierarchy → clique direito → Create Empty
Nome: SlimeVerde
Position: X=5, Y=0, Z=5
```

### Passo 2 — Adicionar componentes
```
Add Component → NavMesh Agent
  Speed: 3
  Radius: 0.4

Add Component → Capsule Collider
  Radius: 0.4
  Height: 0.8
  Layer: Targetable   ← ESSENCIAL

Add Component → MonsterEntity
```

### Passo 3 — Configurar MonsterEntity
```
Monster Name:    Slime Verde
Level:           1
Base HP:         120
Base ATK:        12
Base DEF:        3
Aggro Range:     6
Attack Range:    1.8
Kite Distance:   1.2
Attack Cooldown: 2
Exp Reward:      25
```

### Passo 4 — Visual (Capsule filho)
```
Hierarchy → clique direito em SlimeVerde → 3D Object → Sphere
Nome: Visual
Scale: 0.8, 0.8, 0.8
Material: verde (#22BB22)
```

### Passo 5 — Barra de HP
```
Hierarchy → clique direito em SlimeVerde → UI → Canvas
  Render Mode: World Space
  Width: 100, Height: 15
  Scale: 0.01, 0.01, 0.01
  Position: 0, 0.7, 0

Dentro do Canvas → UI → Slider
  Min: 0, Max: 1, Value: 1
  Background: #440000
  Fill: #00CC00

Canvas → Add Component → MonsterHealthBarUI
  HP Slider: arraste o Slider
```

### Passo 6 — Indicador de Seleção
```
Hierarchy → clique direito em SlimeVerde → 3D Object → Cylinder
Nome: SelectionIndicator
Scale: 1, 0.01, 1
Position: 0, 0.01, 0
Material: verde brilhante, Emission ativado

MonsterEntity → Selection Indicator: arraste SelectionIndicator
```

### Passo 7 — Salvar como Prefab
```
Arraste SlimeVerde para Assets/Prefabs/Monsters/
```

### Passo 8 — Testar o dano

Com o personagem de exemplo (Level 1, STR=17 para Anão):
```
ATK  = (17×2) + (12×1) + 1 = 47
DEF do Slime = 3

Dano = 47 × (1 - 3/103) = 47 × 0.971 = ~45

Com Skill "Golpe Básico" (Multiplier 1.0): ~45 de dano
Com Skill "Golpe Pesado" (Multiplier 2.2): ~99 de dano
```

Slime tem 120 HP → morre em 2-3 golpes pesados.

---

# PARTE 8 — CALIBRANDO O COMBATE

## 8.1 O Mob está muito fácil?
- Aumente `Base HP`, `Base ATK`, `Base DEF`
- Reduza o `Attack Cooldown` (ataca mais rápido)
- Aumente o `Aggro Range` (detecta de mais longe)

## 8.2 O Mob está muito difícil?
- Reduza `Base ATK`
- Aumente o `Kite Distance` (fica mais longe do player)
- Aumente o `Attack Cooldown` (ataca mais devagar)

## 8.3 As Skills estão fracas?
- Aumente o `Atk Multiplier` da skill
- Reduza o `Mana Cost`
- Reduza o `Cooldown`

## 8.4 Tabela de referência — Dano por Level

| Level Player | STR | ATK estimado | Dano no Slime (DEF 5) |
|---|---|---|---|
| 1 | 17 | 47 | ~43 |
| 5 | 22 | 59 | ~56 |
| 10 | 32 | 75 | ~72 |
| 20 | 52 | 115 | ~109 |
| 50 | 102 | 255 | ~243 |

## 8.5 Gizmos para visualizar os ranges

No Unity Editor, selecione o mob na Hierarchy:
- 🟡 **Esfera Amarela** = Aggro Range (detecta player aqui)
- 🔴 **Esfera Vermelha** = Attack Range (ataca aqui)
- 🔵 **Esfera Azul**    = Kite Distance (não chega mais perto que isso)

Use esses Gizmos para posicionar os monstros e garantir boa jogabilidade.

---

# PARTE 9 — CHECKLIST FINAL

Antes de testar, confirme tudo:

### Layers
- [ ] Layer `Terrain` criada e aplicada no chão
- [ ] Layer `Targetable` criada e aplicada no CapsuleCollider dos mobs
- [ ] PlayerController tem Terrain Layer e Targetable Layer configurados

### NavMesh
- [ ] Terrain marcado como Navigation Static
- [ ] NavMesh baked (Window → AI → Navigation → Bake)
- [ ] Mob tem NavMeshAgent configurado

### Player Prefab
- [ ] PlayerEntity.cs presente
- [ ] SkillSystem.cs presente com 4 skills configuradas
- [ ] PlayerController.cs presente com layers corretas
- [ ] NavMeshAgent presente

### Monstros
- [ ] MonsterEntity.cs configurado (nome, HP, ATK, DEF, ranges)
- [ ] CapsuleCollider na layer Targetable
- [ ] SelectionIndicator filho configurado
- [ ] HealthBar filho configurado (MonsterHealthBarUI)

### HUD (Canvas)
- [ ] UIManager.cs com HP/MP bars configuradas
- [ ] 4 SkillSlots configurados com SkillSlotUI.cs
- [ ] SkillSlots conectados ao UIManager no campo Skill Slots
- [ ] FloatingTextManager na cena com prefab configurado

### FloatingText Prefab
Crie um prefab simples:
```
FloatingText (GameObject vazio)
└── Text (TMP_Text)
    Font Size: 24
    Bold: sim
    Alignment: Center
    Color: branco
    Outline: preto, espessura 0.3
```
Arraste para o campo `Floating Text Prefab` do FloatingTextManager.

---

# RESUMO ULTRA RÁPIDO

```
1. Crie o mob:
   GameObject vazio
   + NavMeshAgent (Speed: 3)
   + CapsuleCollider (Layer: Targetable)
   + MonsterEntity (configure HP/ATK/DEF/Ranges)
   + Filho visual (Sphere/Capsule colorida)
   + Filho Canvas WorldSpace com Slider (HP bar)
   + Filho Cylinder achatado (seleção)

2. Configure as skills no SkillSystem do player:
   Q: Golpe (Physical, Range 2.5, Mult 1.0, CD 1.5, MP 5)
   W: Pesado (Physical, Range 2.0, Mult 2.2, CD 8.0, MP 15)
   E: Fogo   (Magical,  Range 8.0, Mult 1.8, CD 5.0, MP 20)
   R: Cura   (Heal,     Range 0.0, Mult 1.0, CD 15,  MP 30)

3. Crie layers Terrain e Targetable
   Aplique nos objetos certos

4. Bake o NavMesh

5. Teste:
   Clique no mob → anel verde aparece
   Pressione Q → player anda até o mob
   Chegou → ataca automaticamente
   Números flutuantes aparecem
   Mob morre → XP aparece
```
