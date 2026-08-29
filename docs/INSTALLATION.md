# Instalação

## Para quem só quer usar

1. Abra a [release estável mais recente](https://github.com/ogabriels2/MiguelDownloader/releases/latest).
2. Baixe `MiguelDownloaderApp-win-Setup.exe`.
3. Execute o instalador e abra o Miguel Downloader.

Não é preciso instalar .NET, FFmpeg, yt-dlp, Node, Deno nem configurar `PATH`. Tudo está incluído.

## O que o instalador faz

O instalador Velopack registra o aplicativo no Windows, cria o atalho do Menu Iniciar com a
identidade usada pelas notificações e instala o updater fora da pasta que será substituída. A
instalação padrão é por usuário e não exige uma senha de administrador.

O primeiro uso funciona offline. A internet só é necessária para analisar e baixar mídia, buscar
metadados, atualizar o mecanismo de download e verificar novas versões do aplicativo.

## Atualizações automáticas

A opção vem ligada. No máximo uma vez por dia, o aplicativo consulta o canal estável no GitHub.
Não há conta, telemetria, identificador de instalação ou token embutido.

Quando existe uma versão nova, ela é baixada em segundo plano e verificada. O programa em execução
e a fila não são alterados. A troca acontece após o aplicativo sair e termina na próxima
inicialização. Se houver downloads ativos, eles não são interrompidos para reiniciar.

O comportamento pode ser desativado em **Configurações > Avançado > Atualizações do aplicativo**.
A verificação manual fica em **Ajuda > Verificar atualizações do aplicativo**.

O canal estável ignora releases marcadas como pré-release. Se um pacote diferencial falhar, o
atualizador baixa e verifica automaticamente o pacote completo.

## Onde ficam os dados

| O quê | Caminho |
|---|---|
| Programa instalado | `%LOCALAPPDATA%\MiguelDownloaderApp\current` |
| Pacotes e updater | `%LOCALAPPDATA%\MiguelDownloaderApp` (gerenciado pelo Velopack) |
| Configurações | `%APPDATA%\MiguelDownloader\settings.json` |
| Banco (histórico e fila) | `%LOCALAPPDATA%\MiguelDownloader\migueldownloader.db` |
| Logs | `%LOCALAPPDATA%\MiguelDownloader\logs\` |
| Atualizações das ferramentas | `%LOCALAPPDATA%\MiguelDownloader\tools\` |
| Temporários de download | `%LOCALAPPDATA%\MiguelDownloader\work\` |
| Downloads | Pasta escolhida; por padrão `Downloads\Miguel Downloader` |

Os dados ficam fora da pasta `current`. Atualizar, reparar ou substituir a versão do aplicativo
não toca no histórico, na fila nem nas preferências.

## Versão portátil

Baixe `MiguelDownloaderApp-win-Portable.zip`, descompacte em uma pasta gravável e execute
`Miguel Downloader.exe`. O pacote contém o runtime .NET e todas as ferramentas.

A versão portátil oficial também recebe atualizações pelo mesmo canal. Configurações e histórico
continuam nas pastas do usuário e são compartilhados com uma instalação normal na mesma conta do
Windows.

Ela não aparece em **Aplicativos instalados**. Notificações do Windows podem depender de um atalho
registrado; quando o Windows recusa o toast, a mensagem dentro do app e o progresso na barra de
tarefas continuam funcionando.

## Desinstalação

Abra **Configurações do Windows > Aplicativos > Aplicativos instalados > Miguel Downloader >
Desinstalar**.

Os binários e atalhos são removidos. Configurações, histórico, fila, logs e downloads são dados do
usuário e ficam separados; remova as pastas da tabela apenas se também quiser apagar esses dados.

## Requisitos

- Windows 10 versão 1809 (build 17763) ou superior, 64 bits;
- aproximadamente 700 MB livres;
- internet para baixar mídia e receber atualizações, não para a primeira instalação.

## Integridade e SmartScreen

Cada release inclui `checksums.txt`, hashes SHA-256 no feed Velopack e uma atestação de
proveniência do GitHub. Consulte [RELEASE.md](RELEASE.md) para os comandos de verificação.

Os pacotes 1.0.0 não têm certificado Authenticode comercial. Por isso o SmartScreen pode avisar
que o editor é desconhecido. Isso é diferente da verificação de integridade: hashes e atestação
detectam adulteração, enquanto Authenticode associa o binário a uma identidade reconhecida pelo
Windows. A automação já aceita Azure Artifact Signing ou `signtool` quando um certificado estiver
disponível.
