# Checklist de testes manuais

Os testes automatizados cobrem a lógica pura e o caminho principal de download
(`dotnet test`). Esta lista cobre o que só dá para verificar com a mão e o olho: interação,
casos de rede real e situações que dependem do estado da máquina.

Use conteúdo que você tenha direito de baixar. Onde a lista pede um vídeo específico, o
[Big Buck Bunny](https://www.youtube.com/watch?v=aqz-KE-bpKQ) (Blender Foundation,
Creative Commons) serve para quase tudo: tem 4K, 60 FPS, é longo o bastante para dar tempo
de cancelar, e é livre para uso.

Legenda: ☐ não testado · ✅ passou · ❌ falhou (anote o que aconteceu)

---

## 1. Conteúdo básico

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 1 | ☐ Vídeo comum | Cole a URL de um vídeo e analise | Thumbnail, título, canal e duração corretos |
| 2 | ☐ Short | URL `youtube.com/shorts/...` | Reconhecido como **Short** |
| 3 | ☐ Vídeo 1080p | Escolha 1080p e baixe | Arquivo abre em 1080p |
| 4 | ☐ Vídeo 4K | Escolha 2160p e baixe | Arquivo abre em 2160p; leva mais tempo |
| 5 | ☐ Vídeo 60 FPS | Verifique a lista de qualidade | Aparece "1080p60"/"2160p60", não só "1080p" |
| 6 | ☐ Vídeo HDR | Analise um vídeo HDR | Marcado como HDR; "Preferir HDR" seleciona a versão HDR |
| 7 | ☐ Áudio separado | Baixe qualquer 1080p+ | Etapas mostram "Baixando vídeo" → "Baixando áudio" → "Combinando faixas" |
| 8 | ☐ Múltiplas faixas de áudio | Vídeo com dublagem (ex.: trailers oficiais dublados) | Lista de idiomas aparece; escolher 2+ força MKV e avisa |
| 9 | ☐ Legendas | Vídeo com legendas | Lista separa **oficial** de **automática**; incorporar funciona |

## 2. Música

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 10 | ☐ Música | URL de `music.youtube.com` | Abre já no modo **Somente áudio** |
| 11 | ☐ Álbum | URL de álbum (`OLAK5uy_...`) | Lista as faixas na ordem certa |
| 12 | ☐ Playlist de músicas | Playlist do YT Music | Itens selecionáveis; ordem preservada |
| 13 | ☐ Metadados | Baixe uma faixa e veja as propriedades no Explorer | Título, artista, álbum, nº da faixa e ano preenchidos |
| 14 | ☐ Capa | Mesma faixa | Capa aparece no Explorer e no player |
| 15 | ☐ Organização em pastas | Baixe um álbum com "Organizar em pastas" ligado | `Artista/Álbum (Ano)/01 - Faixa.ext` |
| 16 | ☐ Conversão MP3 | Escolha MP3 | Arquivo toca; propriedades mostram MP3 |
| 17 | ☐ Preservar Opus/M4A | Escolha "Manter original" | Extensão `.opus`/`.m4a`; etapa de conversão **não** aparece |
| 18 | ☐ Aviso lossy→lossless | Escolha FLAC | Texto explica que FLAC não recupera qualidade perdida |

## 3. Coleções

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 19 | ☐ Playlist de vídeos | Playlist com 10+ itens | Contagem correta; selecionar/desmarcar funciona |
| 20 | ☐ Confirmação em massa | Playlist acima do limite (padrão 20) | Diálogo pergunta antes de enfileirar |
| 21 | ☐ Canal | `youtube.com/@canal/videos` | Lista carrega; avisa se foi truncada |
| 22 | ☐ Numeração | Baixe uma playlist | Arquivos numerados `01 - `, `02 - ` na ordem da lista |
| 23 | ☐ Playlist parcial | Playlist com um vídeo removido | Os outros baixam; só o indisponível falha |

## 4. Fila e controle

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 24 | ☐ Progresso real | Baixe um arquivo grande | Velocidade, tamanho e ETA mudam de verdade |
| 25 | ☐ Etapas nomeadas | Observe a fila | "Baixando vídeo" → "Combinando faixas" → "Verificando arquivo" |
| 26 | ☐ Cancelar | Cancele no meio | Para em segundos; **nenhum** arquivo na pasta de destino |
| 27 | ☐ Pausar e continuar | Pause e continue | Retoma perto de onde parou, não do zero |
| 28 | ☐ Simultâneos | Enfileire 5 com limite 2 | Só 2 rodam por vez |
| 29 | ☐ Tentar novamente | Force um erro, clique em repetir | Reinicia limpo |
| 30 | ☐ Menu de contexto | Botão direito num item | Abrir arquivo/pasta, copiar URL, remover |

## 5. Erros e situações adversas

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 31 | ☐ URL inválida | Digite `abc` e analise | "Esta URL não é válida" + "Confira o endereço" |
| 32 | ☐ URL não-YouTube | Cole um link do Vimeo | Recusada com explicação |
| 33 | ☐ URL de busca | Cole `youtube.com/results?search_query=x` | Explica que busca não é baixável |
| 34 | ☐ Vídeo indisponível | `watch?v=AAAAAAAAAAA` | "Este vídeo não está disponível" — **não** um erro genérico |
| 35 | ☐ Vídeo privado | URL de vídeo privado | "Este vídeo é privado" + sugestão de cookies |
| 36 | ☐ Sem internet | Desligue a rede e analise | "Falha de conexão. Verifique sua internet." |
| 37 | ☐ Queda no meio | Desligue a rede durante um download | Falha com erro de rede; repetir funciona ao voltar |
| 38 | ☐ Disco cheio | Aponte para um pendrive quase cheio | Avisa **antes** de começar |
| 39 | ☐ Pasta sem permissão | Aponte para `C:\Windows\System32` | "Sem permissão para escrever nessa pasta" |
| 40 | ☐ Arquivo já existe | Baixe o mesmo vídeo duas vezes | Pergunta: renomear / substituir / cancelar |
| 41 | ☐ Arquivo em uso | Abra o arquivo num player e rebaixe substituindo | "O arquivo está aberto em outro programa" |
| 42 | ☐ Sem ferramentas | Apague `%LOCALAPPDATA%\Acervo\tools` e abra | Oferece baixar; instala e volta a funcionar |

## 6. Persistência

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 43 | ☐ Fila sobrevive ao fechamento | Enfileire, feche, reabra | Itens pendentes voltam |
| 44 | ☐ Confirmação ao fechar | Feche com download ativo | Pergunta antes de encerrar |
| 45 | ☐ Configurações persistem | Mude pasta e tema, reabra | Mantidos |
| 46 | ☐ Histórico persiste | Baixe, feche, reabra, veja o histórico | Entrada presente com caminho e tamanho |
| 47 | ☐ Arquivo movido | Baixe, mova o arquivo, veja o histórico | Marca "Arquivo não encontrado"; abrir fica desabilitado |
| 48 | ☐ Sem lixo temporário | Após downloads normais | `%LOCALAPPDATA%\Acervo\work` vazio |

## 7. Interface

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 49 | ☐ Tema claro/escuro | Alterne nas configurações | Muda na hora; contraste bom nos dois |
| 50 | ☐ Seguir o Windows | Deixe "Seguir o Windows" e mude o tema do sistema | Acompanha sem reiniciar |
| 51 | ☐ Idioma | Troque para inglês e reabra | Interface toda em inglês, sem chaves cruas |
| 52 | ☐ Teclado | Navegue só com Tab/Enter/setas | Todos os controles alcançáveis; foco visível |
| 53 | ☐ Atalhos | `Ctrl+L`, `Ctrl+V`, `Ctrl+J`, `Ctrl+,`, `Delete` | Todos funcionam |
| 54 | ☐ Arrastar e soltar | Arraste um link do navegador | Preenche a URL |
| 55 | ☐ Área de transferência | Ligue a detecção e copie um link | **Oferece** analisar; não baixa sozinho |
| 56 | ☐ Redimensionar | Encolha até a largura mínima | Nada corta nem sobrepõe |
| 57 | ☐ DPI | Teste em 100%, 150% e 200% | Texto e ícones nítidos |
| 58 | ☐ Leitor de tela | Ligue o Narrador | Campos e botões anunciados com nome |
| 59 | ☐ Estados vazios | Fila e histórico vazios | Mensagem explicativa, não uma área em branco |

## 7b. Instalação e dependências

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 71 | ☐ Instalação limpa | Execute o Setup num Windows sem .NET | Instala sem pedir nada além de confirmar |
| 72 | ☐ Sem administrador | Instale com conta padrão | Não pede elevação |
| 73 | ☐ Primeira execução pronta | Abra logo após instalar | Nenhum painel de "instalar ferramentas"; já dá para colar URL |
| 74 | ☐ Sem PATH | Abra com o PATH vazio | Funciona igual |
| 75 | ☐ Atalho e ícone | Menu Iniciar, Alt+Tab, barra de tarefas | Ícone correto em todos |
| 76 | ☐ Aplicativos e Recursos | Configurações do Windows | Aparece com nome, ícone, versão e editor |
| 77 | ☐ Desinstalar | Pelo painel do Windows | Remove tudo; pergunta sobre os dados; padrão é manter |
| 78 | ☐ Reinstalar por cima | Instale de novo sem desinstalar | Atualiza no lugar, mantém histórico |
| 79 | ☐ Portátil noutro PC | Copie a pasta para outro Windows | Funciona sem instalar nada |
| 80 | ☐ Reparar ferramentas | Apague `<instalação>\tools` e abra | Oferece baixar; funciona depois |

## 7c. Prioridades e HDR

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 81 | ☐ Prioridade muda a escolha | Alterne Qualidade ↔ Compatibilidade | O formato escolhido muda de verdade |
| 82 | ☐ Compatibilidade prefere H.264 | Num vídeo com VP9 4K e H.264 1080p | Escolhe H.264, mesmo custando resolução |
| 83 | ☐ HDR só aparece quando existe | Analise um vídeo SDR | O controle de HDR não é exibido |
| 84 | ☐ HDR aparece quando existe | Analise um vídeo HDR | Controle visível, com três opções |
| 85 | ☐ Preferir SDR evita HDR | Vídeo HDR, escolha "Preferir SDR" | Baixa a versão SDR |
| 86 | ☐ HDR preservado | Vídeo HDR, "Preservar" ou "Preferir HDR" | `ffprobe` no arquivo mostra HDR preservado |
| 87 | ☐ Playlist grande carregada por completo | Canal com mais de 200 vídeos | Mostra o parcial e oferece carregar tudo |
| 88 | ☐ Seleção preservada ao carregar tudo | Desmarque itens, carregue tudo | As exclusões continuam desmarcadas |

## 8. Honestidade dos dados

Estes são os que mais importam — o programa não deve afirmar nada que não tenha medido.

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 60 | ☐ Sem resolução fantasma | Analise um vídeo só até 720p | 1080p/4K **não** aparecem na lista |
| 61 | ☐ Bitrate real de áudio | Veja o modo avançado | ~130 kbps no máximo — nunca "320 kbps" |
| 62 | ☐ Tamanho desconhecido | Um formato sem tamanho informado | Mostra nada (ou `~`), nunca um número inventado |
| 63 | ☐ Progresso indeterminado | Download sem tamanho total | Barra indeterminada, não uma porcentagem falsa |
| 64 | ☐ Aviso de reconversão | Force MP4 num vídeo VP9/Opus | Avisa que haverá reconversão **antes** de baixar |
| 65 | ☐ Sem reconversão no padrão | Deixe o contêiner em "Automático" | Diz "sem reconversão"; a etapa de conversão não aparece |
| 66 | ☐ Concluído = verificado | Baixe qualquer coisa | Só marca concluído após "Verificando arquivo" |

## 9. Segurança e privacidade

| # | Caso | Como testar | O que deve acontecer |
|---|---|---|---|
| 67 | ☐ Título hostil | Baixe um vídeo com `/ \ : * ? " < > \|` no título | Nome de arquivo válido, sem perder o sentido |
| 68 | ☐ Título não-ASCII | Vídeo com título em japonês/árabe/acentos | Caracteres preservados corretamente |
| 69 | ☐ Diagnóstico limpo | Exporte o diagnóstico e abra o `.zip` | Sem cookies, sem tokens, sem URLs de stream, usuário substituído por `[user]` |
| 70 | ☐ Logs limpos | Abra o log mais recente | Nenhuma URL `googlevideo.com` (elas contêm seu IP) |

---

## Como reportar uma falha

1. Configurações › Avançado › **Exportar diagnóstico**
2. Anote o número do caso, a URL usada (se puder compartilhar) e o que esperava
3. O `.zip` já vem limpo de dados sensíveis — confira antes de enviar se quiser
