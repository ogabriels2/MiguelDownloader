# Miguel Downloader

Baixador de vídeos e músicas para Windows: YouTube, YouTube Music, TikTok, X, Instagram,
Facebook, Reddit, SoundCloud e centenas de outros sites.
Aplicativo desktop nativo (WPF, .NET 8), com interface em português e inglês.

**Instale e use.** Não é preciso instalar .NET, FFmpeg, yt-dlp, Node, Deno, nem configurar
PATH. Tudo vai junto no instalador.

O programa analisa a URL **antes** de baixar, oferece apenas os formatos que a fonte realmente
publica, e só marca um download como concluído depois de verificar o arquivo final com
`ffprobe`.

---

## Sumário

- [Instalação](#instalação)
- [Visão geral](#visão-geral)
- [Redes suportadas](#redes-suportadas)
- [Plataformas de música](#plataformas-de-música-spotify-apple-music-deezer-tidal-amazon-music)
- [Princípios](#princípios)
- [Como usar](#como-usar)
- [Arquitetura](#arquitetura)
- [Compilar e testar](#compilar-e-testar)
- [Onde ficam os arquivos](#onde-ficam-os-arquivos)
- [Limitações reais](#limitações-reais)
- [Documentação](#documentação)
- [Autoria](#autoria)
- [Licença](#licença)
- [Componentes de terceiros](#componentes-de-terceiros)
- [Uso responsável](#uso-responsável)

---

## Instalação

1. Baixe `MiguelDownloaderApp-win-Setup.exe` na [release mais recente](https://github.com/ogabriels2/MiguelDownloader/releases/latest)
2. Execute
3. Abra o Miguel Downloader

Instala por usuário, sem pedir administrador, e mantém o aplicativo atualizado pelo canal estável
do GitHub. Detalhes, versão portátil e desinstalação em
[INSTALLATION.md](docs/INSTALLATION.md).

**Requisitos:** Windows 10 versão 1809 (64 bits) ou superior, ~700 MB de espaço.

---

## Visão geral

| Recurso | Situação |
|---|---|
| Vídeos, Shorts, transmissões gravadas | Funciona |
| Playlists, álbuns, canais | Funciona, testado com 5.000 itens |
| YouTube Music (faixas e álbuns) | Fluxo dedicado, com metadados e capa |
| Resolução, FPS, codec, HDR | Só o que existe para aquele vídeo |
| Presets de prioridade | Qualidade / Compatibilidade / Menor tamanho, com regras distintas |
| HDR | Política de três estados: preservar, preferir HDR, preferir SDR |
| Múltiplas faixas de áudio (dublagens) | Detectadas e selecionáveis |
| Legendas (oficiais, automáticas, traduzidas) | Baixar, incorporar ou salvar à parte |
| Fila com pausar / continuar / cancelar / repetir | Funciona |
| Retomada após fechar o programa | Fila e configurações persistem |
| Histórico pesquisável | SQLite, com abrir arquivo/pasta e baixar de novo |
| Notificações do Windows | Na versão instalada (ver limitações) |
| Tema claro / escuro / seguir o Windows | Funciona |
| Português (Brasil) e inglês | Completo |
| Atualizações automáticas | GitHub Releases estável, com hash, retomada e aplicação após sair |

---

## Redes suportadas

O reconhecimento de link não é uma lista fechada. O `yt-dlp` embarcado traz mais de **1.700
extratores** e ganha novos entre uma versão e outra deste app, então um endereço desconhecido é
**tentado** em vez de recusado — travar numa lista escrita à mão transformaria link que funciona
em "site não suportado" só porque este arquivo não foi atualizado.

O que o app conhece pelo nome serve para falar com confiança sobre o link antes de perguntar:
mostrar a origem, avisar que um site costuma exigir sessão, e abrir no fluxo de música quando o
site só serve áudio.

### Medido de verdade, com o yt-dlp que vai no instalador

| Rede | Análise | Download | O que a fonte entrega |
|---|---|---|---|
| YouTube | sim | sim | vídeo e áudio separados, até 2160p60, **HDR10** |
| YouTube Music | sim | sim | áudio, metadados e capa |
| TikTok | sim | sim | vídeo já com áudio (h264/h265), até 1024p |
| X (Twitter) | sim | sim | vídeo separado, sem login para post público |
| Facebook | sim | sim | vídeo e áudio separados (VP9) |
| Reddit | sim | sim | vídeo e áudio separados |
| SoundCloud | sim | sim | só áudio — abre direto no fluxo de música |
| Instagram | exige sessão | com cookies | login obrigatório na maioria dos posts |
| Vimeo | exige sessão | com cookies | o cliente web recusa acesso anônimo |

Cada linha "sim" foi obtida rodando o aplicativo instalado, baixando um arquivo real e
conferindo o resultado com `ffprobe`. Nada aqui foi copiado de documentação.

### Sobre HDR e qualidade nas outras redes

**HDR é uma propriedade da fonte, não do programa.** O YouTube publica faixas HDR10; TikTok,
X, Facebook e Reddit, nas medições feitas, publicam apenas SDR. Nenhuma engenharia inventa
HDR onde ele não existe, e o app não finge que existe: os controles de HDR aparecem quando a
fonte oferece uma faixa HDR e ficam de fora quando não oferece.

O mesmo vale para resolução, FPS, faixas de áudio alternativas, legendas e tamanho de arquivo.
O que muda entre as redes é o que elas entregam — a lógica de seleção, o remux sem reencode, a
verificação com `ffprobe` e a honestidade sobre tamanho são as mesmas em todas.

### Sites que pedem sessão

Instagram e Vimeo recusam a maior parte do acesso anônimo. O app avisa **antes** da tentativa e
aponta para a configuração de cookies — mas não bloqueia nada: parte do conteúdo público
funciona sem sessão, e o único jeito de saber é tentar.

Cookies ficam em Configurações › Avançado, lendo do navegador ou de um arquivo. Use apenas para
conteúdo a que você já tem acesso.
---

## Plataformas de música (Spotify, Apple Music, Deezer, TIDAL, Amazon Music)

Colar um link dessas plataformas **não baixa áudio delas**, e isso não é contornável: todas
criptografam o áudio — Widevine no Spotify, TIDAL e Amazon, FairPlay na Apple, Blowfish no Deezer.
As ferramentas que baixam de lá funcionam quebrando essa criptografia com segredo obtido por
engenharia reversa, chave extraída de um aparelho Android físico, ou um cliente se passando pelo
aplicativo oficial. Este programa não faz isso — e distribuir uma ferramenta que faz é proibido
pela DMCA §1201(a)(2) e pela Lei 9.610/98 Art. 107.

O que **é** público é o catálogo. É ele que o app lê.

### O que acontece ao colar o link

1. O lançamento é resolvido: título, artista, capa em resolução máxima e a lista completa de faixas
2. Cada faixa ganha seus identificadores — **ISRC**, número de faixa e disco, selo, código de
   barras, gênero, copyright, BPM — mais os identificadores canônicos do MusicBrainz, buscados
   **por ISRC** (chave exata, não busca por nome)
3. Ao baixar, cada gravação é localizada em fontes que servem áudio abertamente
4. O arquivo sai com todas essas tags e a capa embutida

### O que cada plataforma publica

| Plataforma | Catálogo | Como |
|---|---|---|
| **Deezer** | completo | API pública, sem credencial — inclui ISRC de cada faixa |
| **Apple Music** | completo | API do iTunes, sem credencial — capa em 1400×1400 |
| **Spotify** | nome e artista | só isso anonimamente; a lista de faixas vem de outro catálogo aberto |
| **TIDAL** | nome | renderiza no navegador; mesma ponte |
| **Amazon Music** | nome | idem |

Quando a lista de faixas veio de outro catálogo, **o app avisa na tela**. É quase sempre o álbum
certo e às vezes não é, e quem olha percebe na hora.

### Onde as gravações são procuradas, e em que ordem

Primeiro onde pode haver **sem perdas**, depois o resto:

1. **Bandcamp** — FLAC, WAV, ALAC e AIFF quando o artista publicou com preço zero
2. **Internet Archive** — FLAC comprovado (medido: 16 bits/44,1 kHz)
3. **YouTube** e **SoundCloud** — Opus ~128 kbps e AAC 160 kbps, com perdas

Havendo acerto confiável sem perdas, a busca para ali. Caso contrário cai na retaguarda e **diz
que é com perdas** — nunca rotula Opus como lossless.

Uma verdade desconfortável mas honesta: para catálogo de gravadora grande **não existe rota
lossless gratuita**. Daft Punk vem do YouTube em Opus. Música independente, netlabels e shows
autorizados vêm em FLAC.

### O que impede baixar a faixa errada

A **duração decide**. Buscar uma faixa do Daft Punk no SoundCloud devolve o upload oficial do
próprio artista — com 30 segundos, porque é prévia. Ele ganha em artista, em título e em ser
oficial; só o comprimento o denuncia. Por isso duração fora de 12 segundos **recusa** o candidato
em vez de apenas penalizá-lo.

Medido num álbum completo de 14 faixas: todas dentro de **1 segundo** da duração informada.

### Velocidade

Parear 14 faixas em sequência levava 2min56s antes de baixar um byte. Hoje: **primeiro arquivo em
21,7 s**, álbum inteiro em 89 s. As buscas vão em lote (abrir o yt-dlp custa ~6 s independente do
número de resultados), candidatos são confirmados com uma requisição barata antes de qualquer
coisa cara, um álbum ausente das fontes lossless para de ser procurado após 3 tentativas, e o
primeiro lote é pequeno de propósito.

Nada disso mudou **o que** é aceito — só quando a busca para de procurar.

---

## Princípios

Três decisões guiaram o resto do projeto.

**1. Nunca reconverter sem necessidade.** O contêiner é escolhido para caber nas faixas
originais. Reconversão só acontece quando você fixa um formato incompatível — e o programa
avisa antes, explicando o custo. No modo Automático há uma garantia testada: nenhuma
combinação de codecs produz reconversão.

**2. Não afirmar o que não foi medido.** Isso vale para tamanho, bitrate, resolução, FPS, HDR,
codec e duração. O tamanho, por exemplo, tem quatro estados distintos:

```
Tamanho: 1,34 GB              todos os componentes reportaram valor exato
Tamanho estimado: ~1,3 GB     algum valor era estimativa da fonte
Tamanho: pelo menos 9,8 MB    algum componente não reportou nada
Tamanho desconhecido          nada reportou
```

O terceiro estado existe porque uma versão anterior anunciava "9,8 MB" para um download 4K de
mais de um gigabyte — somando só o áudio, que era a única parte com tamanho conhecido.

**3. Concluído significa verificado.** Um download só é marcado como concluído depois que o
`ffprobe` confirma que o arquivo abre, contém as faixas pedidas e tem duração compatível com a
fonte.

---

## Como usar

Na primeira vez que o programa abre, um guia de cinco telas percorre esse caminho e mostra a pasta
de destino que está valendo. Ele não volta a aparecer sozinho, e fica em **Ajuda > Guia rápido**.

1. Cole uma URL (`Ctrl+V`, que cola e analisa de uma vez) ou arraste um link para a janela
2. Escolha **Vídeo** ou **Somente áudio**, a prioridade e o formato
3. Leia a linha de observação, que diz se haverá reconversão e por quê
4. **Baixar**

### Prioridades

Não existe uma ordenação única correta de formatos, então a prioridade muda as regras:

| Prioridade | O que vem primeiro |
|---|---|
| **Qualidade máxima** | Resolução, HDR, FPS, bitrate. Codec só desempata |
| **Compatibilidade** | H.264, AAC, SDR, MP4 — mesmo que custe resolução |
| **Menor tamanho** | Codec eficiente e o menor arquivo ainda assistível |

Num mesmo vídeo, Qualidade e Compatibilidade escolhem formatos diferentes de propósito, e há
teste garantindo que continuem discordando.

### HDR

Três estados, em vez de uma caixa de seleção ambígua:

- **Preservar o que existir** (padrão): HDR não é buscado nem evitado
- **Preferir HDR**: escolhe a versão HDR quando a fonte tiver
- **Preferir SDR**: evita HDR, útil quando HDR exigiria conversão

O controle só aparece quando o vídeo realmente tem versão HDR. Nenhuma conversão HDR→SDR
acontece em silêncio: quando for necessária, é dita antes.

### Atalhos

| Atalho | Ação |
|---|---|
| `Ctrl+L` | Focar o campo de URL |
| `Ctrl+V` | Colar e focar a URL |
| `Ctrl+J` | Ir para a fila |
| `Ctrl+,` | Configurações |
| `Enter` | Analisar |
| `Delete` | Remover o item selecionado da fila |

---

## Arquitetura

```
MiguelDownloader.App  (WPF, MVVM)      interface, view models, serviços de aplicação
    ├── MiguelDownloader.Engine        processos externos, fila, pipeline de download
    │      └── MiguelDownloader.Core
    ├── MiguelDownloader.Data          SQLite (histórico e fila), settings em JSON
    │      └── MiguelDownloader.Core
    └── MiguelDownloader.Core          domínio puro, sem UI, sem IO, sem rede
```

Detalhes e o raciocínio por trás das decisões em [ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Distribuição

Todas as builds são **self-contained**. Uma instalação limpa do Windows traz .NET Framework 4.8,
que é um produto diferente e não executa .NET 8 — um pacote dependente do framework falharia
exatamente nas máquinas que precisamos suportar.

yt-dlp, FFmpeg, ffprobe e o runtime JavaScript (Deno) vão dentro do instalador, verificados por
SHA-256 durante o build. Ver [DEPENDENCIES.md](docs/DEPENDENCIES.md).

O Velopack gera o instalador, o pacote portátil e o feed usado pelo atualizador. Os dados do usuário
ficam fora da raiz `%LOCALAPPDATA%\MiguelDownloaderApp` que ele gerencia, então desinstalar ou
substituir binários não mistura programa, banco, fila e logs.

O canal estável é o [GitHub Releases](https://github.com/ogabriels2/MiguelDownloader/releases).
Uma verificação diária baixa em segundo plano apenas uma versão mais nova e não considera
pré-releases. Pacotes parciais podem ser retomados; tamanho e SHA-256 são conferidos; deltas voltam
automaticamente ao pacote completo se não puderem ser aplicados. A troca só ocorre após o app
sair e nunca interrompe um download ativo para reiniciar.

O `build/publish.ps1` grava `checksums.txt`. O workflow também publica proveniência Sigstore dos
artefatos e monta a release como rascunho antes de publicá-la completa. Dependências transitivas
ficam travadas em `packages.lock.json` e as Actions oficiais são fixadas por SHA.

A identidade visual é gerada, não desenhada à mão. `build/make-icon.ps1` produz o ícone em nove
tamanhos e os PNGs que a interface e o instalador usam.

Cada tamanho do ícone é renderizado na própria escala em vez de reamostrado a partir de um
desenho grande, porque a 16 pixels o vão entre a seta e a prateleira fecha e o símbolo vira
mancha. As entradas vão em BMP até 64 pixels e PNG em 128 e 256, que é a composição que o próprio
Windows usa: um arquivo só de PNG fica menor, mas decodificadores antigos não o leem.

A cor de destaque dourada é aplicada pelo `ThemeService` a cada troca de tema, e não uma vez na
inicialização: aplicar um tema no WPF-UI redefine o destaque para o do Windows.

#### O instalador não é assinado

Sem assinatura digital, o Windows exibe "O Windows protegeu o seu computador" na primeira
execução, e quem instala precisa clicar em "Mais informações" e depois em "Executar assim mesmo".
Isso não indica problema no programa; o SmartScreen apenas não reconhece o editor.

Resolver isso depende de um certificado de assinatura de código emitido por autoridade
certificadora, com validação de identidade e custo anual. Certificados OV levam algumas semanas
acumulando reputação antes de o aviso parar de aparecer; certificados EV, entregues em token
físico, dispensam essa espera. A Microsoft também oferece o Azure Trusted Signing, com preço
menor e regras de elegibilidade próprias. Os valores e as condições mudam, então vale consultar
antes de escolher.

O empacotamento aceita Azure Artifact Signing ou parâmetros de `signtool` por variáveis de
ambiente; Velopack assina o executável, o updater e o instalador nos pontos corretos. Nenhuma chave
ou senha entra no repositório. Consulte [RELEASE.md](docs/RELEASE.md).

---

## Compilar e testar

```bash
git clone https://github.com/ogabriels2/MiguelDownloader.git
cd MiguelDownloader
dotnet restore --locked-mode
dotnet build
dotnet run --project src/MiguelDownloader.App
```

```bash
dotnet test --filter "Category!=Integration&Category!=ToolInstall&Category!=Stress"   # 380
dotnet test --filter "Category=Stress"        # filas de 1.000 e 5.000 itens
dotnet test --filter "Category=Integration"   # YouTube real
```

Gerar os pacotes:

```powershell
pwsh build/publish.ps1
```

Ver [TESTING.md](docs/TESTING.md) e [RELEASE.md](docs/RELEASE.md).

---

## Onde ficam os arquivos

| O quê | Onde |
|---|---|
| Programa instalado | `%LOCALAPPDATA%\MiguelDownloaderApp\current` |
| Updater e pacotes | `%LOCALAPPDATA%\MiguelDownloaderApp` |
| Ferramentas gerenciadas | `%LOCALAPPDATA%\MiguelDownloader\tools` |
| Configurações | `%APPDATA%\MiguelDownloader\settings.json` |
| Banco | `%LOCALAPPDATA%\MiguelDownloader\migueldownloader.db` |
| Logs | `%LOCALAPPDATA%\MiguelDownloader\logs` |
| Downloads | Pasta escolhida (padrão `Downloads\Miguel Downloader`) |

---

## Limitações reais

Limitações verdadeiras, não pendências disfarçadas.

1. **As outras redes entregam menos que o YouTube, e isso não tem conserto do lado de cá.**
   Nas medições, só o YouTube publica HDR. TikTok, X, Facebook e Reddit publicam SDR, e o
   TikTok chega a 1024p contra 2160p60 do YouTube. O programa mostra o que a fonte tem e
   omite o que ela não tem, em vez de exibir controle que não faz nada.

2. **Instagram e Vimeo exigem sessão para quase tudo.** Sem cookies configurados, a análise
   falha com "É necessário estar autenticado". Isso é decisão desses sites. Com os cookies do
   navegador apontados em Configurações, funciona — para o conteúdo a que você já tem acesso.

3. **Extrator quebra sem aviso.** Estes sites mudam o HTML e a API quando querem. Numa das
   medições um endereço de vídeo do Facebook falhou com "Cannot parse data" enquanto os Reels
   funcionavam; o app classifica isso como ferramenta desatualizada e sugere atualizar, que é
   o conserto que costuma valer. Manter o yt-dlp em dia é parte de usar o programa.

4. **Bloqueio por endereço de rede acontece.** O TikTok recusou um post citando o IP, e o
   YouTube passa a pedir verificação depois de muitas requisições seguidas. Não é defeito do
   app; ele distingue esse caso de restrição regional para não mandar você atrás de VPN sem
   necessidade.

5. **Notificações do Windows só na versão instalada.** Elas exigem um atalho de Menu Iniciar
   carregando o AppUserModelID, que o instalador cria. A versão portátil não tem esse atalho e
   usa a mensagem na janela e o progresso na barra de tarefas — que funcionam sempre. O
   aplicativo detecta a diferença sozinho e não tenta de novo depois da primeira recusa.

6. **Teste em Windows realmente limpo não foi executado.** Foi provado, com o aplicativo
   instalado e `PATH` vazio, que **nenhum** módulo é carregado de `C:\Program Files\dotnet` e
   que as ferramentas resolvem para as cópias empacotadas. Isso mede exatamente a dependência
   que importa, mas não é um Windows recém-instalado. O Windows Sandbox não está habilitado
   nesta máquina e habilitá-lo exige reinicialização. `build/clean-machine-test.wsb` e
   `build/clean-machine-test.ps1` deixam o teste pronto para rodar.

7. **Pausar encerra o processo e retoma do arquivo parcial.** O yt-dlp não tem sinal de pausa.
   Na prática se perde no máximo o último fragmento. Formatos HLS retomam com menos precisão
   que downloads HTTP diretos.

8. **DRM não é contornado.** Streams com DRM são detectados, excluídos das opções e reportados.

9. **Mixes (`RD...`) não têm fim definido.** São suportados, com aviso de que a lista é gerada
   dinamicamente.

10. **Playlists muito grandes são listadas com limite** (200 itens na primeira passagem) para
   não gastar minutos enumerando um canal inteiro antes de você decidir.

11. **Limite de velocidade é por download**, não global.

12. **Binários não assinados.** O SmartScreen alerta na primeira execução. Assinatura exige
   certificado comercial.

---

## Documentação

| Documento | Assunto |
|---|---|
| [INSTALLATION.md](docs/INSTALLATION.md) | Instalar, atualizar, desinstalar e usar o portátil |
| [DEPENDENCIES.md](docs/DEPENDENCIES.md) | Cada dependência, origem, verificação, atualização |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Decisões arquiteturais e o porquê |
| [TESTING.md](docs/TESTING.md) | Suítes, números medidos, o que foi e não foi provado |
| [RELEASE.md](docs/RELEASE.md) | Processo de release, versionamento, migrações |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Sintomas e o que fazer |
| [MANUAL-TESTS.md](docs/MANUAL-TESTS.md) | 70 casos de teste manual |

---

## Autoria

Miguel Downloader é obra de **Gabriel Silva Dias Moreira**.

Copyright © 2026 Gabriel Silva Dias Moreira. Todos os direitos reservados.

Site: [ogabriels.com](https://ogabriels.com)
Contato: contato@ogabriels.com

---

## Licença

O programa é gratuito para uso pessoal e profissional, e não é software livre: o código é do
autor, e redistribuir versão alterada, vender ou remover os avisos de autoria depende de
autorização escrita.

Os termos completos estão em [`legal/LICENCA.txt`](legal/LICENCA.txt), que o instalador exibe
antes de instalar e deixa na pasta do programa. O menu **Ajuda > Licença de uso** abre esse mesmo
arquivo.

O que a licença garante a você:

- instalar em quantos computadores forem seus, sem prazo e sem pagar;
- guardar cópias de segurança do instalador, direito que o art. 6º, I, da Lei 9.609/98 assegura;
- repassar o instalador original a outra pessoa, desde que sem cobrar e sem alterá-lo.

O programa não coleta dados, não tem telemetria e não envia nada ao autor. A seção 8 da licença
detalha o que fica gravado no seu computador e em que situações o programa acessa a rede.

---

## Componentes de terceiros

O instalador leva programas de outros autores, cada um sob a própria licença. A relação completa,
com versões, titulares e endereços do código-fonte, está em
[`legal/AVISOS-DE-TERCEIROS.txt`](legal/AVISOS-DE-TERCEIROS.txt), instalado junto com o programa
e acessível pelo menu **Ajuda > Componentes de terceiros**.

| Componente | Licença | Papel |
|---|---|---|
| yt-dlp | Unlicense | identifica e transfere a mídia |
| FFmpeg, ffprobe | GPL-3.0 | remuxa, converte e valida o arquivo final |
| Deno | MIT | resolve o JavaScript que alguns sites exigem |
| TagLib# | LGPL-2.1 | grava metadados nos arquivos de áudio |
| WPF-UI | MIT | controles e visual Fluent |
| .NET 8, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite | MIT | runtime e infraestrutura |
| Serilog | Apache-2.0 | registros de execução |
| Velopack | MIT | instalador e atualizações transacionais |
| SQLite | domínio público | histórico e fila |

Dois deles dão direitos a quem recebe o programa.

**FFmpeg** está sob GPL-3.0. O texto da licença acompanha a instalação em `licencas\GPL-3.0.txt`,
e você tem direito ao código-fonte correspondente. Ele está publicado em
[yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds/releases), na mesma página de cada
versão, e também pode ser pedido a contato@ogabriels.com por três anos a contar da data em que
você recebeu sua cópia. O Miguel Downloader executa o FFmpeg como processo separado, sem
vinculação de código.

**TagLib#** está sob LGPL-2.1, que assegura a você substituir a biblioteca por uma versão sua.
Por isso o programa é publicado sem empacotamento em arquivo único: `TagLib.dll` fica solto na
pasta e pode ser trocado.

---

## Uso responsável

Este programa transfere arquivos. Quem fornece o conteúdo é o site cujo endereço você cola nele.

Baixe apenas o que você tem direito ou autorização de baixar: seus próprios vídeos, material em
domínio público, obra com licença aberta, conteúdo que o titular liberou ou situação que a lei
permita. Os termos de uso do site também valem, e às vezes proíbem o download mesmo quando a lei
não proíbe.

O programa não contorna DRM. Onde a plataforma cifra o arquivo, o download não acontece, e
nenhuma configuração muda isso.
