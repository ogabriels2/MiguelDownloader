# Histórico de versões

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e versionamento
conforme [SemVer](https://semver.org/lang/pt-BR/).

## [1.0.0] — 28 de agosto de 2026

Primeira versão pública estável.

### Download e mídia

- YouTube, YouTube Music, Instagram, X, TikTok, Facebook, Reddit, SoundCloud e os demais
  extratores compatíveis com yt-dlp.
- Vídeos individuais, playlists, canais e coleções grandes, com carregamento progressivo.
- Análise prévia de resolução, FPS, HDR, codec, contêiner, faixas de áudio e legendas.
- Presets distintos para melhor qualidade, compatibilidade e menor tamanho.
- Fila persistente com pausa, retomada, cancelamento, repetição, limite de concorrência e
  histórico pesquisável.
- Workspace isolado por download e validação final com ffprobe antes de concluir.

### Música

- Fluxo dedicado para YouTube Music, com capa, metadados, ISRC, álbum e numeração de faixa.
- Catálogos públicos de Spotify, Apple Music, Deezer, TIDAL e Amazon Music usados para localizar
  a gravação correspondente em fontes que o aplicativo consegue baixar sem contornar DRM.
- MP3, M4A, Opus, FLAC, WAV e preservação do stream original.

### Aplicativo

- Interface WPF nativa em português e inglês, temas claro/escuro/sistema, guia inicial, atalhos,
  barra de menus, barra de status, notificações e diagnóstico exportável sem dados sensíveis.
- Instância única para impedir duas filas concorrendo pelo mesmo banco.
- Configurações gravadas de forma atômica e banco SQLite com migrações transacionais.
- Pacotes autocontidos para Windows 10/11 x64 com .NET 8, yt-dlp, FFmpeg, ffprobe e Deno.

### Atualizações e distribuição

- Instalação e pacote portátil gerenciados por Velopack.
- Verificação diária do canal estável no GitHub Releases, sem token ou telemetria.
- Download em segundo plano com retomada, trava de concorrência, SHA-256, pacotes diferenciais e
  fallback automático para o pacote completo.
- Aplicação transacional apenas após o aplicativo sair; downloads ativos nunca são interrompidos
  para reiniciar.
- Release automatizada por tag, dependências .NET e ferramentas nativas travadas, CI, checksums
  SHA-256, proveniência Sigstore do GitHub e atualização semanal via Dependabot.

[1.0.0]: https://github.com/ogabriels2/MiguelDownloader/releases/tag/v1.0.0
