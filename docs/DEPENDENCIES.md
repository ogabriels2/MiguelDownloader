# Dependências

Este documento lista tudo de que o Miguel Downloader depende, de onde vem, como é verificado
e como chega à máquina do usuário.

**Resumo:** o usuário não instala nada além do Miguel Downloader. Não há .NET para instalar,
nem FFmpeg, nem yt-dlp, nem Node, nem Deno, nem PATH para configurar.

---

## Por que isso precisou ser corrigido

A primeira versão afirmava que o WPF rodava sobre um runtime que "já acompanha o Windows".
Isso estava **errado na prática**, e a correção é a mudança mais importante desta fase.

O que o Windows realmente traz de fábrica é o **.NET Framework 4.8** — um produto diferente,
que não executa aplicações .NET 8. Verificado nesta máquina:

```
.NET Framework instalado (registro NDP\v4\Full): Release 0x8234d  → 4.8.1
.NET 8 Desktop Runtime:                          presente apenas porque o SDK foi instalado
```

Um pacote *framework-dependent* falharia ao iniciar exatamente nas máquinas limpas que
precisamos suportar. Por isso **todas as builds agora são self-contained**: o runtime .NET
viaja dentro do pacote.

---

## Inventário

| Componente | Versão | Licença | Como chega | Verificação |
|---|---|---|---|---|
| .NET 8 Desktop Runtime | 8.0.x | MIT | Embutido na build (self-contained) | Publicado pelo SDK |
| yt-dlp | 2026.08.19 | Unlicense | Empacotado no instalador | SHA-256 vs `SHA2-256SUMS` |
| FFmpeg | N-126312-gdf48dc624e-20260828 | GPL-3.0 | Empacotado no instalador | SHA-256 vs `checksums.sha256` |
| ffprobe | idem FFmpeg | GPL-3.0 | Empacotado no instalador | idem |
| Deno (runtime JS) | 2.9.6 | MIT | Empacotado no instalador | SHA-256 vs `.sha256sum` |
| SQLite (e_sqlite3) | via SQLitePCLRaw | Domínio público | Embutido na build | Publicado pelo SDK |
| WPF-UI | 4.3.0 | MIT | Assembly gerenciado na build | NuGet |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Assembly gerenciado na build | NuGet |
| Serilog | 4.4.0 | Apache-2.0 | Assembly gerenciado na build | NuGet |
| TagLibSharp | 2.3.0 | LGPL-2.1 | Assembly gerenciado na build | NuGet |
| Microsoft.Data.Sqlite | 8.0.30 | MIT | Assembly gerenciado na build | NuGet |
| Velopack | 1.2.0 | MIT | Instalador e assembly gerenciado | NuGet / ferramenta fixada |

Nenhum deles é instalado no sistema. Nada entra no PATH global. Nada é registrado
globalmente além da identidade de notificação (ver [INSTALLATION.md](INSTALLATION.md)).

---

## Por que Deno, e não Node ou QuickJS

O yt-dlp precisa de um runtime JavaScript para resolver os desafios que o YouTube apresenta.
Sem um, ele emite um aviso e **alguns formatos podem não aparecer** — o que viola diretamente
o princípio de mostrar a qualidade realmente disponível.

Foram avaliados três:

| Runtime | Tamanho | Situação no yt-dlp | Decisão |
|---|---|---|---|
| **Deno 2.9.6** | 42,7 MB zip / 92,9 MB extraído | Recomendado, habilitado por padrão | **Escolhido** |
| Node.js 22+ | ~80 MB | Suportado, precisa ser habilitado | Não empacotado |
| QuickJS-NG 0.16.2 | 2,15 MB | Suportado | Recusado |

QuickJS é tentadoramente pequeno, mas a documentação do yt-dlp registra que ele **não consegue
baixar dependências EJS do npm**, além de precisar criar arquivos temporários a cada execução.
O primeiro ponto é um risco real: se o yt-dlp passar a precisar de um pacote npm para algum
desafio, o QuickJS falha e o usuário perde formatos sem entender por quê.

Como o requisito explícito é *confiabilidade acima de tamanho*, o Deno foi empacotado.

Node **não** é instalado globalmente. Se a máquina já tiver Node ou Deno no PATH, o programa
os detecta e usa, mas isso é apenas um caminho alternativo — a cópia empacotada é o padrão.

---

## Verificação de integridade

Cada binário baixado durante o build é conferido contra o checksum que o próprio projeto
publica. Uma divergência **aborta o build**; um executável não verificado não é empacotado.

As versões, os artefatos, os hashes dos arquivos publicados e os hashes de todos os executáveis
e DLLs extraídos ficam fixados em `build/tools.lock.json`. Uma mudança upstream não altera uma
release silenciosamente: a build falha até que o lock seja revisado e atualizado de propósito.

Um detalhe que só apareceu ao implementar: **os três projetos publicam checksums em formatos
diferentes**, e presumir um só teria falhado silenciosamente.

```
yt-dlp         SHA2-256SUMS       <hash>  yt-dlp.exe            (estilo coreutils)
FFmpeg-Builds  checksums.sha256   <hash>  ffmpeg-...zip         (estilo coreutils)
Deno           ...zip.sha256sum   Hash      : <HASH>            (Get-FileHash do PowerShell)
```

O leitor em [`build/fetch-tools.ps1`](../build/fetch-tools.ps1) aceita os três.

O download só acontece por HTTPS; uma URL sem HTTPS é recusada antes de qualquer requisição.
HTTPS sozinho, porém, **não é tratado como prova de integridade** — o checksum é obrigatório.

---

## Onde as ferramentas ficam

```
<pasta de instalação>\tools\        ← empacotadas pelo instalador (padrão)
%LOCALAPPDATA%\MiguelDownloader\tools\   ← atualizações baixadas pelo app
```

A ordem de resolução é:

1. Caminho configurado manualmente em Configurações › Avançado
2. Cópia gerenciada em `%LOCALAPPDATA%` (é aqui que uma atualização do yt-dlp aterrissa)
3. **Cópia empacotada** na pasta de instalação
4. PATH do sistema

A cópia empacotada nunca é sobrescrita por uma atualização. Isso é intencional: ela é o
fallback conhecidamente funcional. Se uma atualização do yt-dlp quebrar, apagar a pasta
gerenciada devolve o programa ao estado que veio no instalador.

Como a instalação é **por usuário** (`%LOCALAPPDATA%\MiguelDownloaderApp\current`), o
aplicativo consegue atualizar a si próprio e suas ferramentas sem pedir elevação. Os dados
continuam separados em `%LOCALAPPDATA%\MiguelDownloader`, fora da raiz gerenciada pelo
Velopack.

---

## Atualização das ferramentas

| Item | Comportamento |
|---|---|
| Versão instalada | Mostrada em Configurações › Avançado |
| Verificação manual | Botão "Verificar atualizações agora" |
| Verificação automática | Opcional, a cada N dias (padrão 3) |
| Origem | Releases oficiais do projeto, por HTTPS |
| Integridade | SHA-256 obrigatório |
| Rollback | A versão anterior é preservada como `yt-dlp.exe.old` |
| Falha | Registrada no log; a versão funcional anterior permanece |

Uma atualização que não passa no checksum é **descartada** — a cópia em uso não é tocada.

FFmpeg e Deno não são atualizados automaticamente: mudam pouco, são grandes, e uma
atualização malsucedida do FFmpeg quebraria mux e conversão. Eles acompanham a versão do
aplicativo.

---

## Licenças

`tools\licenses\FFmpeg.txt` acompanha a instalação e registra, para os binários empacotados:
a release exata, o SHA-256, e os endereços do build e do código-fonte.

O FFmpeg é GPL-3.0. O Miguel Downloader **executa** o ffmpeg como processo separado e não faz
linkagem com ele; os binários são redistribuídos sem modificação, com a atribuição e os
ponteiros para o código-fonte exigidos pela licença.

---

## O que o usuário precisa ter

Apenas:

- Windows 10 versão 1809 (build 17763) ou superior, 64 bits
- ~700 MB de espaço para a instalação, mais espaço para os downloads
- Conexão com a internet **para baixar vídeos** (não para instalar)

A instalação em si funciona offline: nada é baixado durante ela.
