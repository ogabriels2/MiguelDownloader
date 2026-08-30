# Microsoft Store

## Modelo de distribuição

O Partner Center reservou estas identidades; cada caractere precisa permanecer idêntico no pacote:

| Campo | Valor |
|---|---|
| Store ID | `9NDM7323X3HN` |
| Package/Identity/Name | `ogabriels.MiguelDownloader` |
| Package/Identity/Publisher | `CN=3A079706-B200-4828-AA8B-78BC8F2289C0` |
| PublisherDisplayName | `ogabriels` |

O MSIX é deliberadamente produzido sem certificado do desenvolvedor. O Partner Center valida o
pacote e a Microsoft aplica a assinatura de produção depois da certificação. Não publique esse MSIX
sem assinatura como download comum: ele existe para ser enviado ao Partner Center.

Dentro de qualquer pacote MSIX, o aplicativo detecta a identidade atribuída pelo Windows e desativa
o Velopack e a troca autônoma dos executáveis auxiliares. O pacote inteiro passa a ser atualizado por
sua origem — Microsoft Store em produção. Caminhos personalizados, ferramentas do `PATH` e argumentos
extras do yt-dlp também deixam de influenciar a execução: somente os executáveis contidos no diretório
assinado do pacote são aceitos. A versão EXE/portátil continua usando o canal estável do GitHub
normalmente e preserva suas opções avançadas.

## Gerar o pacote

```powershell
pwsh build/publish-store.ps1 -Version 1.0.0
```

O processo:

1. executa os testes automatizados;
2. restaura uma versão fixada do `Microsoft.Windows.SDK.BuildTools`;
3. usa a trava exclusiva `build/store/tools.lock.json`, confere por SHA-256 yt-dlp, o build
   compartilhado LGPL do FFmpeg/ffprobe, Deno e todas as DLLs e rejeita arquivos extras no cache;
4. executa o FFmpeg incluído, confirma a licença LGPL e produz/prova um vídeo H.264 real;
5. publica o aplicativo .NET 8 completo para `win-x64`;
6. injeta a identidade e as imagens da Store;
7. cria o MSIX com MakeAppx;
8. desempacota o resultado e confere novamente nome, editor e versão;
9. grava o SHA-256 ao lado do pacote.

Saída: `artifacts/store/MiguelDownloader-<versão>.0-x64.msix`.

O workflow `Microsoft Store package` repete esse processo em um Windows limpo a cada tag estável e
guarda o MSIX como artefato privado da execução por 30 dias. Ele não anexa o pacote sem assinatura à
release pública.

## Publicar uma atualização

1. altere `<Version>` em `Directory.Build.props` e as notas da versão;
2. faça merge na `main` somente após CI verde;
3. crie a tag `vX.Y.Z`;
4. baixe o artefato do workflow `Microsoft Store package`;
5. crie um novo envio do produto `9NDM7323X3HN`, envie o MSIX e atualize a descrição se necessário;
6. revise preços, mercados, classificação etária e declarações; depois envie à certificação.

Depois que o primeiro envio estiver certificado, a API de submissão do Partner Center pode ser
associada a uma identidade do Microsoft Entra para automatizar os passos 4–6. Isso não é habilitado
no primeiro lançamento porque exige credenciais persistentes, permissões da conta e confirmação das
declarações de cada versão. Se for adotado, prefira credenciais federadas do GitHub OIDC em vez de um
segredo duradouro.

## Teste e assinatura

`MakeAppx pack` e uma leitura completa com `MakeAppx unpack` validam estrutura e conteúdo sem alterar
o repositório de certificados do computador. Instalar o pacote localmente exigiria confiar em um
certificado de teste; isso não é necessário para o envio e não deve ser confundido com a assinatura
de produção da Microsoft.

## URLs públicas da listagem

- Privacidade: <https://migueldownloader.ogabriels.com/privacidade.html>
- Suporte: <https://migueldownloader.ogabriels.com/suporte.html>
- Site: <https://migueldownloader.ogabriels.com/>

## Perfil de licença do FFmpeg

A distribuição pelo site continua usando o build GPL-3.0 do `yt-dlp/FFmpeg-Builds`, que inclui
x264/x265. O pacote da Microsoft Store usa separadamente o build compartilhado
`ffmpeg-master-latest-win64-lgpl-shared.zip` do `BtbN/FFmpeg-Builds`, sob
LGPL-3.0-or-later. Isso evita impor os termos padrão da Store sobre um binário GPLv3.

Quando o usuário da edição Store escolhe manualmente um contêiner que obriga recodificação, o app
seleciona `libopenh264` para MP4 e `libvpx-vp9` para WebM. O fluxo automático continua preferindo
remux sem perda, como nas outras distribuições.
