# Decisões arquiteturais

Este documento registra as decisões que mais moldaram o Miguel Downloader e o raciocínio por
trás delas — inclusive onde a implementação se afastou do que foi pedido, e onde uma decisão
anterior estava errada e foi corrigida.

---

## 1. Self-contained, porque o Windows não traz .NET 8

**Esta é a correção mais importante da segunda fase.**

A primeira versão afirmava que o WPF rodava sobre um runtime que "já acompanha o Windows". Isso
está errado na prática. O Windows traz o **.NET Framework 4.8**, que é um produto diferente e
não executa aplicações .NET 8. Verificado nesta máquina:

```
NDP\v4\Full Release = 0x8234d  → .NET Framework 4.8.1
.NET 8 Desktop Runtime         → presente só porque o SDK foi instalado
```

Um pacote *framework-dependent* falharia ao iniciar exatamente nas máquinas limpas que o
programa precisa suportar. Todas as builds agora são **self-contained**.

Isso foi verificado, não deduzido. Com o aplicativo instalado e iniciado com `PATH` vazio e
nenhuma variável `DOTNET_*`, os módulos carregados são:

```
coreclr.dll, hostfxr.dll, hostpolicy.dll, PresentationFramework.dll, e_sqlite3.dll
    → todos de C:\...\MiguelDownloaderApp\current\

zero módulos de C:\Program Files\dotnet
```

**Custo aceito:** cerca de 150 MB de runtime por pacote. O requisito era confiabilidade acima de
tamanho.

**Trimming continua desligado.** O WPF resolve tipos por reflexão para XAML e binding; uma build
com trimming compila, inicia, e falha em tempo de execução na tela que ninguém abriu durante o
teste.

---

## 2. WPF em vez de WinUI 3

O enunciado original sugeria WinUI 3. A investigação do ambiente-alvo mudou a conclusão:

| Critério | WinUI 3 | WPF |
|---|---|---|
| Runtime extra exigido do usuário | Windows App SDK | Nenhum (self-contained) |
| Build sem Visual Studio | Frágil | Suportado |
| Mica no Windows 10 | Não se aplica | Idem — empate |
| Maturidade de DataGrid/ListView/binding | Em evolução | Estável |

O alvo primário é Windows 10 22H2, onde os efeitos visuais que justificam o WinUI não existem. O
resultado continua sendo um aplicativo Windows nativo — não um site empacotado.

---

## 3. Instalação por usuário

`%LOCALAPPDATA%\MiguelDownloaderApp\current`, sem elevação, como VS Code e Discord. Três
motivos:

1. Uma pessoa comum instala sem senha de administrador.
2. O aplicativo consegue atualizar seu próprio yt-dlp depois. Em `Program Files` cada
   atualização pediria elevação.
3. A desinstalação não deixa resíduo em áreas do sistema.

Banco, configurações, fila, ferramentas atualizadas e logs ficam na raiz independente
`%LOCALAPPDATA%\MiguelDownloader`. Assim, o atualizador pode trocar o programa de forma
transacional, e uma desinstalação não apaga dados pessoais.

Cada pasta temporária de uma tarefa recebe uma marca de propriedade. A limpeza recursiva recusa
qualquer diretório sem essa marca; quando o usuário escolhe uma raiz temporária, os workspaces
ficam ainda dentro de um filho dedicado `MiguelDownloader-work`. Assim, nem uma configuração
ampla nem um caminho persistido corrompido transforma arquivos alheios em temporários do app.

---

## 4. Ferramentas empacotadas, com fallback conhecidamente funcional

yt-dlp, FFmpeg, ffprobe e Deno vão dentro do instalador, verificados por SHA-256 no build. Nada
é baixado na primeira execução — ela já abre pronta.

A ordem de resolução é: caminho configurado → cópia gerenciada em `%LOCALAPPDATA%` → **cópia
empacotada** → PATH.

A cópia empacotada nunca é sobrescrita por uma atualização. Ela é o estado bom conhecido: se
uma atualização do yt-dlp quebrar, apagar a pasta gerenciada devolve o programa ao que veio no
instalador.

### Deno, não Node nem QuickJS

O yt-dlp precisa de um runtime JavaScript, e sem ele o YouTube retém formatos. QuickJS pesa
2 MB contra 43 MB do Deno, mas a documentação do yt-dlp registra que ele não consegue baixar
dependências EJS do npm — um risco real de perder formatos sem explicação. Com "confiabilidade
acima de tamanho" como regra, o Deno (recomendado pelo próprio yt-dlp) foi empacotado.

---

## 5. Um processo yt-dlp por item

Entregar a playlist inteira ao yt-dlp seria mais simples e custaria: progresso agregado sem
saber em que item está, cancelamento tudo-ou-nada, um vídeo removido derrubando o resto, e
nenhum controle real de simultaneidade.

Um processo por item entrega progresso por item, cancelamento independente, retomada individual
e sucesso parcial — que é o resultado normal de uma playlist grande.

---

## 6. Diretório de trabalho exclusivo por download

Cada job baixa numa pasta vazia própria. O motivo decisivo é **identificar o resultado**: o
yt-dlp decide a extensão final pelo que conseguiu, e pós-processadores trocam o contêiner.
Prever o nome é frágil; como a pasta começou vazia, o arquivo de mídia que apareceu é o
resultado.

Pastas órfãs de um fechamento forçado são varridas na inicialização, sem tocar nas de jobs
vivos.

---

## 7. Formatos fixados por id, sem cadeia de fallback

O download usa exatamente os `format_id` que a interface mostrou. Uma cadeia de fallback
tornaria o download mais resiliente e a interface mentirosa: o usuário escolheria 4K e receberia
1080p sem saber. Se o formato sumiu, o job falha pedindo nova análise.

---

## 8. Sites conhecidos pelo nome, sites desconhecidos ainda assim tentados

Todo link entra pelo `MediaUrlParser`. Ele identifica o site e então delega ao leitor do YouTube
— cujas formas de URL são finitas, documentadas e valem leitura minuciosa, porque saber que um
link é um mix e não uma playlist muda o que o app faz antes de gastar uma requisição — ou
classifica o caminho com a confiança modesta que os outros sites merecem.

**Host desconhecido não é recusado.** O `yt-dlp` embarcado traz mais de 1.700 extratores e ganha
novos entre uma versão deste app e a seguinte. Uma lista fechada aqui transformaria link que
funciona em "site não suportado" só porque este arquivo ficou para trás. Recusa-se apenas o que
não é link.

A assimetria é deliberada: um caminho desconhecido **no YouTube** continua sendo rejeitado,
porque ali um endereço fora do conjunto conhecido está genuinamente errado.

O `ProviderCatalog` guarda só o que decide análise sintática e texto: nome do site, domínios,
segmentos que significam "uma pessoa ou coleção", quais parâmetros de query identificam quem
compartilhou, e se o site costuma exigir sessão. **Nada ali afirma o que um item entrega.**

### Capacidade é do item, não do site

HDR, faixas de áudio alternativas, legendas, resolução e tamanho vêm da resposta do extrator
para aquele item — nunca do nome do site. Dois posts do mesmo site divergem, e o que um site
serve muda sem aviso. Escrever "TikTok não tem HDR" no código seria uma promessa que o código
não pode cumprir; ler `dynamic_range` da resposta é um fato.

Por isso o aviso de sessão é *conselho*, não bloqueio: o botão Analisar continua ativo para
Instagram e Vimeo, porque parte do conteúdo público funciona sem sessão e o único jeito de
saber é perguntar.

### O que a extensão para outras redes revelou

Dois defeitos reais, ambos invisíveis enquanto só existia o YouTube:

**Stream muxado emparelhado com faixa de áudio solta.** O TikTok publica todas as versões de
vídeo já com áudio embutido *e* uma faixa de áudio avulsa ao lado. A condição antiga perguntava
se a fonte tinha áudio separado, então pegava os dois e mandava o ffmpeg sobrepor uma trilha a
outra que já estava no arquivo. A pergunta certa é sobre o stream escolhido. No YouTube as duas
condições coincidem, porque fonte que separa áudio também separa vídeo.

**Nome dimensionado para a pasta errada.** O nome cabia na pasta de destino, mas o download é
construído na pasta de trabalho — mais longa — e o yt-dlp ainda acrescenta o sufixo de formato a
cada parte. Título de Reel do Facebook passa de 170 caracteres; título de YouTube tem uma
fração disso, e por isso o estouro do limite de caminho do Windows só apareceu ao apontar o app
para outro site.

---
## 9. Prioridade depende do preset

Uma revisão desta fase corrigiu uma regra global equivocada ("FPS sempre acima de codec"). Não
existe uma ordenação única correta:

| Prioridade | Ordem |
|---|---|
| **Qualidade** | Resolução → HDR → FPS → bitrate → codec |
| **Compatibilidade** | Codec reproduzível → SDR → resolução → FPS → bitrate |
| **Menor tamanho** | Altura mínima assistível → tamanho → codec eficiente |

Compatibilidade coloca o codec **primeiro** de propósito: um VP9 impecável que o dispositivo
recusa é pior que um H.264 que toca. Há teste garantindo que Qualidade e Compatibilidade
escolham formatos **diferentes** no mesmo vídeo.

O ranking de compatibilidade coloca AV1 acima de HEVC porque o Windows exige uma extensão de
codec paga para HEVC, enquanto AV1 é decodificado nativamente.

---

## 10. HDR é uma política de três estados

`bool PreferHdr` não conseguia distinguir "deixe como está" de "evite ativamente", e são pedidos
diferentes:

| Política | Efeito |
|---|---|
| **Preservar** (padrão) | HDR não é buscado nem evitado |
| **Preferir HDR** | Escolhe a versão HDR quando existir |
| **Preferir SDR** | Evita HDR, para não forçar tone-mapping |

O bug que motivou isso: com o booleano em `false`, o ranking ficava neutro em vez de evitar HDR,
então o preset de compatibilidade ainda podia escolher um stream HDR e cair num transcode. Um
teste pega isso hoje.

Nenhuma conversão HDR→SDR acontece em silêncio.

---

## 11. Tamanho tem quatro estados

```
Exact       todos os componentes reportaram valor exato
Estimated   algum valor era estimativa da fonte
AtLeast     algum componente não reportou nada  → é um piso, não um total
Unknown     nada reportou
```

O estado `AtLeast` existe por causa de um bug real: um download 4K de mais de 1 GB foi anunciado
como "9,8 MB", somando só o áudio, que era a única parte com tamanho conhecido. Somar o que se
sabe e chamar de total erra em ordem de grandeza, não em precisão — por isso a certeza viaja
junto com o número e a interface não consegue exibir um piso como se fosse um total.

---

## 12. Erros: duas passagens

`ErrorClassifier` varre primeiro **só as linhas marcadas como erro**, e só depois o texto
inteiro.

O motivo apareceu ao testar contra o YouTube real: uma execução que falha emite avisos primeiro
e o erro por último. Um vídeo indisponível vinha precedido de um aviso sobre runtime JavaScript,
e a varredura ingênua diagnosticava "instale um runtime" para um vídeo que não existe. Há teste
de regressão para esse caso exato.

---

## 13. Persistência em lote

O coordenador acumula mudanças e grava em lote a cada 3 segundos, em uma transação.

Antes ele gravava por tarefa: enfileirar mil faixas abria mil conexões e confirmava mil
transações. O progresso tem o mesmo problema em outra escala — um download rápido reporta várias
vezes por segundo, e esse dado só importa se o programa for encerrado inesperadamente.

Medido depois da mudança: 5.000 itens salvos em 2,9 s e restaurados em 0,4 s.

A fila persistida **não guarda a lista de formatos**: são centenas de entradas com URLs
assinadas que expiram em horas. Ao restaurar, o item volta como *stub* e os formatos são
buscados de novo — que é exatamente o que um job retomado no dia seguinte precisa.

---

## 14. Listagem grande: rápida primeiro, completa sob demanda

Um canal real usado no teste tem **5.548 vídeos** e leva 255 s para enumerar por inteiro.
Bloquear a interface por quatro minutos antes de o usuário decidir qualquer coisa seria ruim;
esconder o resto seria desonesto.

A primeira passagem lista 200 itens e retorna em segundos. Quando há mais, a interface diz
quantos existem e oferece carregar o restante, preservando as exclusões já feitas.

---

## 15. "Concluído" significa verificado

Um download só é marcado como concluído depois que o `ffprobe` confirma que o arquivo existe,
não está vazio, abre como mídia, contém as faixas pedidas e tem duração compatível com a fonte.

Conferir só o tamanho aceitaria uma transferência truncada ou um contêiner cujo cabeçalho nunca
foi finalizado — a falha que o usuário descobre dias depois.

---

## 16. Notificações do Windows

Toasts exigem que o processo tenha um AppUserModelID **e** que exista um atalho no Menu Iniciar
carregando essa mesma identidade. O instalador cria os dois; a versão portátil não tem como.

O aplicativo tenta uma vez, detecta a recusa e passa a usar a mensagem na janela e o progresso
na barra de tarefas, sem repetir a tentativa. Isso é reportado como limitação da forma de
distribuição, não como recurso ausente.

---

## 17. Segurança

| Risco | Mitigação |
|---|---|
| Injeção via título/URL | `ProcessStartInfo.ArgumentList`; nunca uma linha de comando montada |
| Shell interpretando metacaracteres | `UseShellExecute = false`, sempre |
| URL lida como flag | Tudo depois de `--` é URL |
| Argumentos extras do usuário | Divididos com as regras de aspas do Windows, passados como argv |
| Binário auxiliar adulterado | SHA-256 conferido contra o checksum publicado; divergência aborta |
| Download por HTTP | Recusado; só HTTPS |
| Zip slip | Caminho de destino validado contra a raiz |
| Vazamento no diagnóstico | URLs de stream (que contêm o IP), cookies, tokens e usuário removidos |
| Configuração corrompida | Escrita temporária + `File.Replace` atômico; arquivo ilegível vai para quarentena |
| SQL injection | Consultas parametrizadas; `ORDER BY` de conjunto fechado |
| `LIKE` com curinga | Cláusula `ESCAPE` explícita |

---

## 18. Camadas

```
MiguelDownloader.App  ──►  Engine  ──►  Core
        │                               ▲
        └────────►  Data  ──────────────┘
```

- **Core** não conhece UI, processos, rede nem disco. É onde ficam as regras que valem teste
  exaustivo, e onde está a maior parte dos 380 testes sem rede.
- **Engine** conhece processos e disco, mas não WPF. Os testes de integração a exercitam sem
  abrir janela.
- **Data** conhece SQLite, e nada mais.
- **App** é a única que conhece WPF. A fila emite eventos de threads de trabalho; a
  marshalização acontece aqui.

Um bug desta fase mostrou por que a fronteira importa: `NotificationService` lia
`Application.MainWindow` antes de marshalizar, e essa propriedade é ela própria afinizada à
thread — lançava exceção a cada download concluído. A correção foi mover a busca da janela
**para dentro** da chamada ao dispatcher.

---

## 19. Honestidade como requisito

Três lugares onde foi preciso escolher entre uma interface bonita e uma verdadeira:

**Tamanho desconhecido.** Se qualquer faixa não informa tamanho, o número vira um piso rotulado
como tal.

**Progresso desconhecido.** Sem tamanho total, a barra é indeterminada. Uma porcentagem
inventada é pior que nenhuma.

**Bitrate de áudio.** O YouTube entrega no máximo ~130 kbps, e é isso que aparece. Um teste de
integração falha se o valor observado passar de 200 kbps — se o YouTube mudar, o teste avisa em
vez de a interface começar a mentir.

---

## 20. Atualizações do aplicativo

O aplicativo usa o Velopack com um feed público hospedado em GitHub Releases. A inicialização
do runtime do atualizador ocorre antes do WPF; uma versão instalada consulta apenas o canal
estável e, por padrão, no máximo uma vez a cada 24 horas.

O download acontece em segundo plano. O Velopack valida o SHA-256 publicado no feed, retoma
transferências parciais, prefere deltas e volta ao pacote completo se necessário. A substituição
é aplicada somente depois que o processo fecha. Se houver downloads de mídia ativos, o
aplicativo adia a reinicialização em vez de interrompê-los.

O workflow de release só aceita tags SemVer cuja versão coincide com
`Directory.Build.props`, executa testes e auditoria de dependências, produz os artefatos a
partir de dependências travadas e publica comprovação de procedência (*artifact attestation*).
As releases são preparadas para o modo imutável do GitHub, impedindo a troca silenciosa de um
instalador já publicado.
