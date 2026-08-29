# Solução de problemas

Antes de qualquer coisa: **Configurações › Avançado › Exportar diagnóstico** gera um `.zip` com
os logs recentes e um resumo do ambiente, já limpo de dados sensíveis (nome de usuário
substituído, URLs de stream removidas, cookies e tokens nunca incluídos).

---

## Instalação

**O Windows mostra "O Windows protegeu o computador"**
O SmartScreen alerta porque o instalador não tem certificado de assinatura comercial. Em
**Mais informações › Executar assim mesmo**. Confira o SHA-256 do arquivo contra
`checksums.txt` se quiser ter certeza de que o download não foi adulterado.

**O instalador diz que a versão do Windows não é suportada**
É necessário Windows 10 versão 1809 (build 17763) ou superior, 64 bits. O instalador recusa
versões anteriores em vez de instalar algo que não iniciaria.

**Instalei, mas nada abre**
Verifique `%LOCALAPPDATA%\MiguelDownloader\logs`. Se não houver log algum, o processo nem
chegou a iniciar — normalmente antivírus bloqueando. Se houver um `[FTL]`, o conteúdo dele diz
o motivo.

---

## Primeira execução

**"O mecanismo de download não foi encontrado"**
Isso não deveria acontecer numa instalação normal: as ferramentas vêm no instalador. Se
acontecer, algo removeu `<pasta de instalação>\tools`. Reinstale, ou aponte um caminho manual
em Configurações › Avançado.

**A barra lateral oferece baixar as ferramentas**
Mesma causa: a pasta `tools` sumiu ou está incompleta. O botão baixa e verifica tudo de novo.
Na versão instalada esse painel normalmente nunca aparece.

---

## Análise de URL

| Mensagem | O que significa | O que fazer |
|---|---|---|
| Esta URL não é válida | O texto não é um endereço reconhecido do YouTube | Confira o link |
| Páginas de busca não podem ser baixadas | É uma URL de `/results?search_query=` | Abra um vídeo ou playlist específico |
| Este vídeo não está disponível | Removido, privado ou restrito | Nada a fazer pelo programa |
| Este vídeo é privado | Exige sessão autenticada | Configure os cookies do navegador |
| O YouTube pediu uma verificação | Limite de requisições no seu IP | Espere alguns minutos, ou configure cookies |
| Nenhum formato disponível foi encontrado | Normalmente yt-dlp desatualizado | Configurações › Avançado › Verificar atualizações |
| Este conteúdo é protegido por DRM | Stream com DRM | O programa não contorna DRM |

---

## Downloads

**"O formato escolhido não está mais disponível"**
As URLs de stream do YouTube expiram em algumas horas. Analise de novo e baixe. O programa
fixa exatamente o formato que mostrou, em vez de trocar por outro silenciosamente — por isso
avisa em vez de entregar uma qualidade diferente.

**Download muito lento**
Confira em Configurações › Avançado se o runtime JavaScript aparece. Ele vem no instalador; se
estiver ausente, a decifragem de assinatura pode falhar e o YouTube limita a velocidade.

**Parou no meio e falhou**
Erros de rede são reportados como tal e a fila tenta de novo automaticamente algumas vezes.
"Tentar novamente" retoma a partir do arquivo parcial, não do zero.

**Pausei e o download recomeçou do início**
Pausar encerra o processo e preserva o `.part`; continuar retoma dele. Alguns formatos
fragmentados (HLS) retomam com menos precisão que downloads HTTP diretos, então parte do último
fragmento pode ser refeita.

**"Não há espaço suficiente em disco"**
O programa estima antes de começar, contando vídeo, áudio e o espaço extra que a combinação
consome. Libere espaço ou escolha outra pasta.

**"Sem permissão para escrever nessa pasta"**
Escolha uma pasta dentro do seu perfil de usuário. O programa é instalado sem privilégios de
administrador e não tenta escrever em áreas do sistema.

**"O arquivo está aberto em outro programa"**
Feche o player que está com o arquivo e tente de novo.

---

## Arquivos produzidos

**O arquivo não abre**
Não deveria acontecer: o programa valida com `ffprobe` antes de marcar como concluído. Exporte
o diagnóstico e reporte.

**Baixei em MP4 e a qualidade caiu**
Fixar MP4 num vídeo cujos streams são VP9/Opus obriga a reconverter, e reconversão perde
qualidade. O aviso aparece **antes** do download, na linha de observação. Deixe o contêiner em
**Automático** para evitar isso: nesse modo o programa escolhe um contêiner que aceita as
faixas como estão.

**O vídeo HDR ficou com cores estranhas**
HDR exibido num fluxo SDR parece lavado. Se você escolheu "Preferir SDR" ou o preset de
compatibilidade, o programa baixou a versão SDR de propósito. Para manter HDR, use
"Preservar o que existir" ou "Preferir HDR" e um contêiner que aceite o codec original.

**Converti para FLAC e o arquivo não ficou melhor**
Não fica mesmo, e o programa avisa isso na tela. A fonte do YouTube já é comprimida com perdas;
converter para FLAC evita perdas **adicionais**, mas não recupera o que nunca esteve lá.

**Faltam metadados na música**
O programa só grava campos que a fonte forneceu. Um gênero ou ano ausente significa que o
YouTube não informou — inventar seria pior, porque o dado errado se espalha pela biblioteca.

---

## Interface

**A janela abre cortada ou os textos ficam pequenos**
O aplicativo é per-monitor DPI aware. Se algo parecer errado após mudar a escala do Windows,
feche e reabra.

**Mudei o idioma e nada aconteceu**
O idioma é aplicado na inicialização. Salve e reabra o programa.

**Não recebo notificações do Windows**
Notificações exigem o atalho do Menu Iniciar que o **instalador** cria. A versão portátil não
tem esse atalho e usa a mensagem dentro da janela e o progresso na barra de tarefas. Verifique
também Configurações do Windows › Sistema › Notificações.

---

## Onde olhar

| O quê | Onde |
|---|---|
| Logs | `%LOCALAPPDATA%\MiguelDownloader\logs` |
| Configurações | `%APPDATA%\MiguelDownloader\settings.json` |
| Banco | `%LOCALAPPDATA%\MiguelDownloader\migueldownloader.db` |
| Temporários | `%LOCALAPPDATA%\MiguelDownloader\work` |
| Ferramentas | `<instalação>\tools` e `%LOCALAPPDATA%\MiguelDownloader\tools` |

### Recomeçar do zero

Fechar o programa e apagar `%LOCALAPPDATA%\MiguelDownloader` e
`%APPDATA%\MiguelDownloader` devolve tudo ao estado inicial. Os arquivos já baixados não são
afetados.

### Voltar a uma versão anterior do yt-dlp

Se uma atualização do mecanismo quebrar algo, apague
`%LOCALAPPDATA%\MiguelDownloader\tools`. O programa volta a usar a cópia que veio no
instalador, que é conhecidamente funcional.
