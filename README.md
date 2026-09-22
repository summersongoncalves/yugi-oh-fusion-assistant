# Yu-Gi-Oh! Forbidden Memories — Assistente

Assistente em tempo real para **Yu-Gi-Oh! Forbidden Memories** (PlayStation 1, 2002). Ele observa
a tela do seu emulador, identifica as cartas na sua mão e sugere as fusões de monstro possíveis,
sem ler a memória do emulador, só a tela, então funciona com qualquer emulador de PS1.

> Status: em desenvolvimento ativo. O reconhecimento de cartas e o motor de fusão já funcionam;

> faltando: fusão com cartas mágicas e versão em inglês

1- Abra o app e selecione a janela do emulador

2- Selecione a área das cartas e a área do nome das cartas.

Pronto :D !!!

<img width="800" height="400" alt="UIrecordyugi-oh-ezgif com-video-to-gif-converter" src="https://github.com/user-attachments/assets/b2602a10-f643-4085-8e0c-de9aaeaab191" />

## Baixando o executável pronto

Não precisa instalar nada além do Windows — o executável já vem com tudo que precisa embutido:

1. Baixe a versão mais recente em **[Releases](https://github.com/summersongoncalves/yugi-oh-fusion-assistant/releases/latest)**
   (arquivo `YgoFm-*-win-x64.zip`).
2. Extraia o `.zip` numa pasta qualquer.
3. Rode `YgoFm.App.exe`.

> O arquivo é grande (~130 MB) porque é *self-contained*: embute o runtime do .NET inteiro, então
> ninguém precisa instalar o .NET separadamente só para rodar o app.

## Como funciona (visão geral)

O projeto é dividido em três bibliotecas, cada uma cuidando de uma parte:

```
janela do emulador  →  YgoFm.Vision  →  ids de cartas  →  YgoFm.Core  →  sugestões  →  YgoFm.App
                       (captura, OCR,                     (regras de                  (interface
                        reconhecimento)                    fusão)                      WPF)
```

- **`YgoFm.Core`**  dados puros do jogo: as 722 cartas, a tabela de fusões, e a busca por
  cadeias de fusão sequenciais (o jogo funde cartas na ordem em que são jogadas, não em pares
  soltos). Não sabe nada sobre tela, imagem ou interface.
- **`YgoFm.Vision`**  captura a tela, recorta as regiões marcadas pelo usuário, e reconhece as
  cartas de duas formas complementares:
  1. **Comparação de imagem** contra uma folha de arte oficial das 722 cartas (usando OpenCV).
  2. **Leitura de texto (OCR)** do nome da carta selecionada, que "ensina" uma biblioteca pessoal
     de imagens, construída a partir do seu próprio emulador, o que fica muito mais preciso do
     que comparar contra arte oficial genérica.
- **`YgoFm.App`** a janela WPF: escolher o emulador, marcar as regiões da tela, e mostrar o
  que foi reconhecido e quais fusões são possíveis.

Para o detalhamento técnico completo (por que cada decisão foi tomada, limitações conhecidas,
como o reconhecimento é calibrado), veja [CLAUDE.md](CLAUDE.md).

## Requisitos

- Windows 10 ou mais recente (usa APIs do Windows para captura de tela e OCR).
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Um emulador de PlayStation 1 rodando o jogo (qualquer um: DuckStation, ePSXe, RetroArch, etc.).

## Rodando a partir do código-fonte

Só necessário se for mexer no código — para apenas usar o app, veja
[Baixando o executável pronto](#baixando-o-executável-pronto) acima.

```powershell
git clone https://github.com/summersongoncalves/yugi-oh-fusion-assistant.git
cd yugi-oh-fusion-assistant
dotnet build YgoFm.slnx
dotnet run --project src\YgoFm.App
```

> O arquivo de solução é `YgoFm.slnx`, não `.sln`  é o formato XML novo do .NET 10.
> `dotnet build YgoFm.sln` (com essa extensão) não funciona.

Se estiver usando VS Code, já existe uma configuração de debug pronta em `.vscode/launch.json`
(tecla `F5`).

### Dados que o app usa

A pasta `data/` guarda:
- `cards.json` e `card-art.png`  a base de cartas e a arte oficial, já versionados no repositório.
- `templates/`  a biblioteca pessoal de cartas ensinadas (gerada localmente, nunca commitada).
- `captures/`  capturas de tela salvas para depuração (também nunca commitada).

## Contribuindo

### 1. Abra uma issue antes de começar

Toda mudança: funcionalidade nova, correção de bug, ajuste de reconhecimento, começa com uma
**issue** descrevendo o problema ou a ideia. Isso evita trabalho duplicado e dá espaço para
discutir a abordagem antes de codificar.

### 2. Nomeie a branch a partir da issue

```
<tipo>/<número-da-issue>-descrição-curta
```

Exemplos: `fix/42-ocr-le-pontuacao-junto`, `feat/51-fusao-com-equipamento`.

Tipos usados: `feat` (funcionalidade nova), `fix` (correção), `chore` (manutenção/infra),
`docs` (documentação).

### 3. Commits

- Mensagens no imperativo, explicando o **porquê**, não só o **o quê** (o diff já mostra o quê).
- Referencie a issue quando fizer sentido (ex: `Corrige leitura do OCR incluindo pontuação (#42)`).
- Prefira vários commits pequenos e coerentes a um único commit gigante.

### 4. Pull request

- Título curto, descrevendo a mudança.
- Descrição linkando a issue (`Closes #42`).
- Se mexeu na interface, inclua um print ou GIF do antes/depois.
- Descreva como testou (rodou contra qual emulador, quais cartas/telas verificou).

## Licença

Este projeto é distribuído sob a licença **MIT** — veja [LICENSE.md](LICENSE.md) para o texto
completo.

Isso cobre apenas o código-fonte escrito para este projeto. Os dados das cartas
(`data/cards.json`) e a arte oficial (`data/card-art.png`) continuam sendo propriedade da Konami —
não são cobertos por esta licença, e estão incluídos aqui apenas para o reconhecimento funcionar.
