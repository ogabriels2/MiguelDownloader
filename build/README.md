# build/

Scripts e configuração de empacotamento do Miguel Downloader.

| Arquivo | O que faz |
|---|---|
| `publish.ps1` | Testa e gera instalador, portátil, pacote de atualização e feed Velopack |
| `publish-store.ps1` | Testa e gera o MSIX sem assinatura para o Partner Center |
| `fetch-tools.ps1` | Baixa e verifica yt-dlp, FFmpeg, ffprobe e Deno |
| `tools.lock.json` | Fixa versões, artefatos e SHA-256 das ferramentas da edição do site |
| `store/tools.lock.json` | Fixa o perfil LGPL das ferramentas empacotadas na Microsoft Store |
| `.config/dotnet-tools.json` | Fixa a versão do empacotador Velopack |
| `clean-machine-test.wsb` | Configuração do Windows Sandbox para teste em máquina limpa |
| `clean-machine-test.ps1` | Verificação executada dentro do Sandbox |
| `localization/` | Tabela de strings e gerador dos arquivos `.resx` |
| `tools-cache/` | Ferramentas baixadas (não versionado) |
| `tools-cache-store/` | Ferramentas LGPL da Microsoft Store (não versionado) |

## Uso

```powershell
pwsh build/publish.ps1
```

```powershell
pwsh build/publish-store.ps1 -Version 1.0.0
```

Documentação completa:

- [../docs/RELEASE.md](../docs/RELEASE.md) — processo de release, versionamento, assinatura
- [../docs/MICROSOFT-STORE.md](../docs/MICROSOFT-STORE.md) — MSIX, identidade e atualizações pela Store
- [../docs/DEPENDENCIES.md](../docs/DEPENDENCIES.md) — cada dependência e como é verificada
- [../docs/INSTALLATION.md](../docs/INSTALLATION.md) — instalador e pacote portátil
- [../docs/TESTING.md](../docs/TESTING.md) — suítes e teste em máquina limpa

## Localização

As strings vivem em `localization/strings.json` (uma entrada por chave, com português e inglês).
Depois de editar:

```bash
node build/localization/generate.js build/localization/strings.json \
     src/MiguelDownloader.App/Localization
```

Isso reescreve `Strings.resx` (pt-BR) e `Strings.en.resx`. Os `.resx` são gerados; edite o JSON.

Se uma tradução tiver sido acrescentada diretamente aos `.resx` por uma ferramenta visual,
normalize os dois arquivos de volta para a tabela antes de continuar:

```bash
node build/localization/import-resx.js \
     src/MiguelDownloader.App/Localization/Strings.resx \
     src/MiguelDownloader.App/Localization/Strings.en.resx \
     build/localization/strings.json
```
