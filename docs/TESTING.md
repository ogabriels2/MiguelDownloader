# Testes

```bash
# Rápidos, sem rede — o que roda antes de cada release
dotnet test --filter "Category!=Integration&Category!=ToolInstall&Category!=Stress"

# Contra o YouTube real
dotnet test --filter "Category=Integration"

# Filas e banco em escala (1.000 e 5.000 itens)
dotnet test --filter "Category=Stress"

# Baixa e verifica as ferramentas de verdade (~90 MB)
dotnet test --filter "Category=ToolInstall"
```

| Suíte | Testes | Precisa de rede | Duração típica |
|---|---|---|---|
| Unidade | 380 | Não | ~1 s |
| Integração | 5 | Sim | ~65 s |
| Estresse | 8 | Não | ~7 s |
| Instalação de ferramentas | 4 | Sim | ~95 s |

Os testes de integração usam **Big Buck Bunny** (Blender Foundation, Creative Commons) e só
transferem a menor faixa de áudio.

---

## O que os testes de unidade cobrem

| Área | Exemplos do que é verificado |
|---|---|
| Parsing de URL | 11 formas de URL, handles, tabs de canal, mixes, álbuns, `?t=`, lookalikes recusados |
| Sanitização | Todo caractere que o Windows recusa, nomes reservados, surrogates, limite de caminho |
| Templates | Expansão, separadores órfãos, valores que tentam criar diretórios |
| Seleção de formatos | Tetos, preferências, prioridades por preset, HDR, DRM, storyboards |
| Planejamento de contêiner | Toda combinação codec/contêiner; garantia de que "Automático" nunca reconverte |
| Tamanho | Exato, estimado, piso, desconhecido |
| Progresso | Campos `NA`, fragmentos, pós-processamento |
| Erros | 18 mensagens reais das ferramentas; ordem erro-antes-de-aviso |
| Argumentos | Runtime JS, separador `--`, metacaracteres de shell |
| Banco | Migrations, round-trip, escape de `LIKE` |

---

## Estresse de fila (medido)

Números reais desta máquina (`Category=Stress`):

| Cenário | 1.000 itens | 5.000 itens |
|---|---|---|
| Enfileirar | 5 ms | 2 ms |
| Listar a fila | 3 ms | 1 ms |
| Persistir (uma transação) | 145 ms | 2.931 ms |
| Restaurar do banco | 102 ms | 379 ms |
| Histórico: inserir | 115 ms | 312 ms |
| Histórico: pesquisar | 29 ms | 8 ms |
| Histórico: ordenar por tamanho | 18 ms | 34 ms |
| Memória por item na fila | — | 1.696 bytes (8,1 MB no total) |

### O que esses testes encontraram

**Escrita por item no SQLite.** O coordenador salvava cada tarefa individualmente. Enfileirar
uma playlist de mil faixas abria mil conexões e confirmava mil transações — tempo suficiente
para a interface parecer travada. Agora as mudanças são acumuladas e gravadas em lote a cada
3 segundos, e o fechamento da janela grava tudo o que resta.

Isso vale também para o progresso: um download rápido reporta várias vezes por segundo, e
esse dado só importa se o programa for encerrado inesperadamente. Não há motivo para gravá-lo
a cada atualização.

**Virtualização.** As listas de fila e histórico usam `VirtualizingPanel` com reciclagem, então
5.000 itens não criam 5.000 conjuntos de controles. O teste de memória (1.696 bytes por item)
falha se algo começar a reter a lista de formatos ou um bitmap por linha.

---

## Testes de integração (YouTube real)

| Teste | O que prova |
|---|---|
| `ToolsAreAvailable` | yt-dlp, ffmpeg, ffprobe e o runtime JS resolvem |
| `AnalysesARealVideoAndReportsOnlyRealFormats` | Cada resolução exibida existe; áudio < 200 kbps |
| `DownloadsValidatesAndTagsAudio` | Baixa, valida por ffprobe, duração confere com a fonte |
| `CancellationStopsTheDownloadPromptly` | Cancela em ~120 ms; nenhum arquivo no destino |
| `RejectsAnUnavailableVideoWithAClearReason` | Diagnóstico específico, não erro genérico |

O teste de bitrate é uma trava deliberada: se o valor observado passar de 200 kbps, o teste
falha e obriga a revisar o que a interface afirma sobre qualidade de áudio, em vez de deixá-la
começar a mentir silenciosamente.

---


---

## Aceitação do artefato (`build/verify-artifact.ps1`)

Testes de unidade não enxergam falha de XAML. Um `Style` cujo `BasedOn` aponta para uma chave
registrada para outro tipo de destino compila, publica e instala sem reclamar — e só estoura na
thread de UI quando a lista materializa o primeiro contêiner. Foi exatamente o que aconteceu:
a página de Downloads, justamente onde o usuário acompanha o progresso, subia quebrada enquanto
os testes de unidade continuavam verdes.

Este script é a rede que pega essa classe de defeito. Ele roda contra o que vai ser distribuído,
não contra build de desenvolvimento:

```powershell
# instalação per-user
pwsh build/verify-artifact.ps1

# build portátil já descompactado
pwsh build/verify-artifact.ps1 -AppPath D:\portatil\MiguelDownloader.exe
```

O que ele verifica, em ordem:

| Passo | O que falha o teste |
|---|---|
| Abertura | janela principal não aparece |
| Navegação | item de navegação sem nome acessível, ou página que não abre |
| Análise | URL real não produz resultado baixável |
| Download | nenhum arquivo produzido, arquivo vazio, ou `ffprobe` rejeita |
| Encerramento | janela fecha mas o processo não sai em 30 s |
| Log | qualquer linha `[ERR]` ou `[FTL]` no trecho gravado durante a execução |

A última linha é a mais importante: o programa pode parecer funcionar e mesmo assim ter
registrado exceção. O critério é log limpo, não tela plausível.

O teste de nome acessível existe porque a primeira execução do script revelou que os quatro itens
da navegação chegavam ao leitor de tela como `System.Windows.Controls.ListBoxItem`.

## Verificação de ambiente limpo

O ponto crítico desta fase: provar que o programa não depende do que já existe na máquina de
desenvolvimento.

### O que foi provado

Com o aplicativo **instalado pelo Setup** e iniciado com `PATH` vazio e **nenhuma** variável
`DOTNET_*`:

```
hostfxr.dll                C:\...\MiguelDownloaderApp\current\hostfxr.dll
hostpolicy.dll             C:\...\MiguelDownloaderApp\current\hostpolicy.dll
coreclr.dll                C:\...\MiguelDownloaderApp\current\coreclr.dll
PresentationFramework.dll  C:\...\MiguelDownloaderApp\current\PresentationFramework.dll
e_sqlite3.DLL              C:\...\MiguelDownloaderApp\current\e_sqlite3.DLL

ZERO módulos carregados de C:\Program Files\dotnet
```

E o log do próprio aplicativo:

```
Tools resolved: yt-dlp=2026.08.19 ("Bundled"), ffmpeg=true, ffprobe=true, js="Bundled"
```

Depois disso, um download real foi disparado pela interface do aplicativo instalado e concluído
(11.278.762 bytes), validado por ffprobe.

Isso demonstra, sem depender de interpretação:

- o runtime .NET vem do pacote, não da máquina;
- as ferramentas vêm da instalação, não do PATH;
- o PATH não é consultado para nada;
- a primeira execução já está pronta para uso.

### O que NÃO foi provado

**Não foi executado um teste em uma instalação limpa de Windows num sistema separado.**

O Windows Sandbox não está habilitado nesta máquina (`WindowsSandbox.exe` ausente) e habilitá-lo
exige elevação **e reinicialização**, o que encerraria a sessão de trabalho. A virtualização é
suportada pelo hardware (`VirtualizationFirmwareEnabled = True`), então o caminho está aberto.

O teste de módulos carregados é uma evidência forte e direta — ele mede exatamente a
dependência que importa — mas não substitui um Windows recém-instalado. Para completar:

```powershell
# uma vez, como administrador; exige reinicialização
Enable-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClientVM' -All
```

Depois disso, `build/clean-machine-test.wsb` abre um Windows Sandbox já apontado para a pasta
`artifacts`, e `build/clean-machine-test.ps1` executa a checagem dentro dele.

---

## Testes manuais

A lista completa está em [MANUAL-TESTS.md](MANUAL-TESTS.md) — 70 casos cobrindo conteúdo,
música, coleções, fila, erros, persistência, interface, honestidade dos dados e privacidade.

---

## Quando um teste falha

Trate como bug real até demonstrar que o teste estava errado. Nesta fase, cinco bugs reais
foram encontrados exatamente assim:

| Encontrado por | Bug |
|---|---|
| Executar o aplicativo | Ícone não registrado como recurso WPF: a janela nunca abria |
| Executar o aplicativo | Banco nunca inicializado (`no such table: queue`) |
| Teste de integração | Aviso classificado antes do erro real |
| Captura de tela | Botão Baixar continuava desabilitado após analisar |
| Captura de tela | "9,8 MB" para um download 4K de mais de 1 GB |
| Download real instalado | Exceção de thread ao notificar conclusão |
| Download real instalado | Pasta de download sem espaço no nome |

Dois deles só apareceram ao **olhar a tela** e um só ao **usar a versão instalada** — nenhum
teste automatizado os teria pego.
