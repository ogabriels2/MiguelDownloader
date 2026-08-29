# Processo de release

## Visão geral

O GitHub é a fonte única do canal estável. Uma tag SemVer dispara um runner Windows que restaura
dependências travadas, executa os testes, baixa e verifica as ferramentas externas, publica o app
self-contained, produz os pacotes Velopack, atesta a proveniência, cria uma release em rascunho,
anexa todos os artefatos e só então a publica.

O aplicativo lê apenas releases estáveis do repositório público. Pré-releases são ignoradas.

## Preparar uma versão

1. Atualize `<Version>` em `Directory.Build.props`.
2. Atualize `CHANGELOG.md` e `docs/release-notes.md`.
3. Execute localmente:

   ```powershell
   dotnet restore --locked-mode
   pwsh build/publish.ps1
   ```

4. Revise e envie as mudanças para `main`.
5. Crie e envie a tag correspondente:

   ```powershell
   git tag -s v1.0.1 -m "Miguel Downloader 1.0.1"
   git push origin v1.0.1
   ```

   Se não houver uma chave GPG configurada, use uma tag anotada (`git tag -a`). A release imutável
   e sua atestação ainda vinculam tag, commit e artefatos no GitHub.

O workflow recusa tags cuja versão não seja idêntica à do projeto.

## Artefatos

Velopack produz, no mínimo:

- `MiguelDownloaderApp-win-Setup.exe` — instalador principal;
- `MiguelDownloaderApp-win-Portable.zip` — pacote portátil autoatualizável;
- `MiguelDownloaderApp-1.0.0-full.nupkg` — pacote completo de atualização;
- `MiguelDownloaderApp-1.0.0-delta.nupkg` — diferença para a versão anterior, quando vantajosa;
- `releases.win.json` — índice do canal estável com tamanho, SHA-1 e SHA-256;
- `checksums.txt` — manifesto SHA-256 legível e verificável independentemente.

O workflow baixa a release anterior antes de empacotar. Isso permite gerar deltas, mas o cliente
sempre volta ao pacote completo se o delta estiver ausente, for maior ou não puder ser aplicado.

## Atualizações no cliente

O `AppUpdateService` consulta `GithubSource` no máximo uma vez a cada 24 horas e não incorpora
credencial. Quando encontra uma versão nova:

1. Velopack escolhe delta ou pacote completo;
2. o download pode ser retomado e usa arquivo parcial;
3. tamanho e hash são validados antes de preparar a versão;
4. a versão pronta fica fora da pasta em execução;
5. downloads ativos não são interrompidos;
6. a troca ocorre após a saída, e a inicialização seguinte já usa a versão nova.

A verificação manual fica em **Ajuda > Verificar atualizações do aplicativo** e pode oferecer
reinício imediato quando a fila está ociosa. Desmarcar a atualização automática não impede uma
verificação manual.

## Segurança da cadeia de suprimentos

- `packages.lock.json` fixa dependências diretas e transitivas.
- `build/tools.lock.json` fixa versões, artefatos upstream e hashes de todos os binários nativos.
- CI falha para avisos NuGet NU1901–NU1904.
- GitHub Actions oficiais são fixadas por SHA completo, não por tag mutável.
- Ferramentas empacotadas são baixadas de suas origens e verificadas por SHA-256.
- `actions/attest-build-provenance` publica uma atestação Sigstore dos artefatos.
- A release nasce como rascunho e só é publicada depois que assets, hashes e notas existem.
- Dependabot verifica NuGet, Velopack e GitHub Actions semanalmente.

Depois de criar o repositório, habilite **Settings > Releases > Enable release immutability**. Com
isso, assets e tag ficam bloqueados após a publicação e o próprio GitHub gera uma atestação da
release. Para verificar uma cópia baixada:

```powershell
gh release verify v1.0.0 --repo ogabriels2/MiguelDownloader
gh release verify-asset v1.0.0 .\MiguelDownloaderApp-win-Setup.exe --repo ogabriels2/MiguelDownloader
gh attestation verify .\MiguelDownloaderApp-win-Setup.exe --repo ogabriels2/MiguelDownloader
```

## Assinatura Authenticode

Proveniência e imutabilidade provam de qual workflow veio o arquivo, mas não removem o alerta do
Windows SmartScreen. Para isso é necessário assinar executável, updater e instalador com uma
identidade confiável.

Velopack faz a assinatura no momento correto do empacotamento. Configure uma destas variáveis no
ambiente de build, nunca no repositório:

- `VELOPACK_AZURE_SIGN_FILE` — arquivo de metadados do Azure Artifact Signing;
- `VELOPACK_SIGN_PARAMS` — parâmetros do `signtool`, sem a palavra `sign`.

Azure Artifact Signing é a opção mais automatizável porque a chave fica em HSM gerenciado e o
workflow pode autenticar por OIDC. Enquanto nenhuma identidade de assinatura estiver disponível,
o build continua e avisa explicitamente que os pacotes não são assinados.

## Rollback

Releases publicadas são imutáveis e nunca devem ser substituídas. Se uma versão tiver defeito:

1. desative `latest` ou marque-a como pré-release apenas se a política do GitHub permitir;
2. corrija em uma nova versão patch;
3. publique a nova tag.

Não mova nem reutilize uma tag. Migrações de banco e configurações precisam continuar compatíveis
com versões anteriores; entradas antigas nunca são alteradas depois de chegar aos usuários.

## Build local

```powershell
pwsh build/publish.ps1                     # instalador + portátil + feed
pwsh build/publish.ps1 -Installer          # sem portátil
pwsh build/publish.ps1 -Portable           # sem instalador
pwsh build/publish.ps1 -SkipToolFetch      # reaproveita cache já verificado
```

`-SkipTests` existe apenas para iteração local e não é usado pelo workflow de release.
