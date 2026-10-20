# Tenchu Shadow Reborn — código-fonte v0.2.0

Snapshot dos fontes usados na versão Windows publicada em 20/10/2026, incluindo a interface sem a aba Output e o modo janela com imagem esticada.

## Conteúdo

- `dev/Tenchu-Windows/`: aplicativo, câmera, controles, defesa/parry, stamina, execuções, golpe pesado, desempenho, texturas HD e substituição da malha.
- `dev/SingleFileLauncher/`: fonte do lançador que prepara e abre o jogo a partir do EXE único.
- `dev/RuntimePatch/`: patch de temporização do runtime RecompOne.
- `texture-tools/`: conversão do pacote de texturas para o formato do renderizador.
- `rikimaru-refinado-v1/` e `rikimaru-refinado-texturizado/`: scripts de refinamento da malha e preparação das texturas. Precisam dos arquivos de entrada extraídos localmente; consulte os caminhos nos scripts.
- `SOURCE-MANIFEST.json`: hashes dos arquivos copiados literalmente da árvore de desenvolvimento.

Os arquivos C# são os da versão publicada. O `.csproj` foi adaptado apenas para caminhos relativos das dependências e para localizar `System.IO.Hashing.dll` no SDK, sem o caminho pessoal da máquina de desenvolvimento.

Este é o código do aplicativo e das modificações. Não inclui a imagem do disco, BIOS, código de jogo gerado pela recompilação, saves, logs, modelos extraídos, pacote HD de terceiros ou os fontes completos do motor externo. O motor é uma dependência separada. A arte do ícone é a mesma publicada na apresentação do projeto.

## Compilar no Windows

Requisitos: .NET SDK 10, acesso ao NuGet e as dependências da versão v0.2.0 do runtime. As alterações de gameplay, apesar do prefixo histórico `Benchmark`, fazem parte do jogo: compile com `TenchuBenchmarks=true`, como faz o script abaixo.

1. Extraia este ZIP para uma pasta.
2. Abra uma vez o EXE v0.2.0 e feche o jogo para preparar o runtime local. Ele fica por padrão em `%LOCALAPPDATA%\Tenchu Shadow Reborn\runtime\Tenchu-Shadow-Reborn`.
3. Abra o PowerShell na pasta do código e execute:

```powershell
.\build.ps1 -RuntimeDir "$env:LOCALAPPDATA\Tenchu Shadow Reborn\runtime\Tenchu-Shadow-Reborn"
```

O script copia `RecompOne.Runtime.dll`, `recompone.dll` e os recursos da instalação indicada, restaura os pacotes NuGet e publica o aplicativo em `dist/Tenchu-Shadow-Reborn/`. Os binários do motor são necessários porque os hooks usam APIs e endereços da versão de desenvolvimento correspondente. O patch de temporização já está aplicado ao runtime distribuído; não aplique o mesmo patch duas vezes.

Para usar outro SDK instalado, passe `-DotnetPath 'caminho/para/dotnet.exe'`.

Para iniciar a compilação sem depender do caminho de disco do autor, informe sua própria imagem USA SLUS-00706:

```powershell
$env:TENCHU_DISC_PATH='D:\Jogos\Tenchu\Tenchu.cue'
$env:TENCHU_TEXTURE_PACK_PATH='D:\Texturas\tenchu-native'
& '.\dist\Tenchu-Shadow-Reborn\Tenchu Shadow Reborn.exe'
```

Use o pacote já convertido em `runtime/texture-packs/tenchu-native`, ao lado da instalação preparada pelo EXE, ou converta os arquivos que você possui com `texture-tools/prepare_pack.py`. Texturas externas e disco precisam ser obtidos separadamente. O runtime gera o código do jogo a partir do disco na primeira execução; não coloque a pasta `game/` no Git.

## Lançador e empacotamento

`dev/SingleFileLauncher/Launcher.csproj` contém o lançador Windows Forms. Publique com `dotnet publish -c Release -r win-x64 --self-contained true`. `package.py` é o script usado no layout de desenvolvimento original; ele espera as pastas de distribuição indicadas no início do arquivo. Para redistribuir somente o código, use este ZIP. Para montar um pacote executável local, ajuste os caminhos e forneça seus recursos separadamente.

## Bases e créditos

- Aplicativo base: https://github.com/Heromachine/Tenchu-SA-Recomp — snapshot local no commit `8188535`.
- Recompilador e runtime: https://github.com/BlackLabelHQ/RecompOne. Os binários usados são uma variante do runtime; não há uma cópia completa dos fontes dessa variante neste snapshot. O patch local disponível está em `dev/RuntimePatch/`.
- Dependências: MonoMod, Mono.Cecil, Roslyn, Silk.NET, ImGui.NET, NativeFileDialogSharp e outras listadas no `.csproj`. Cada projeto mantém seus próprios termos e créditos.
- Tenchu e os recursos originais pertencem aos respectivos titulares. O pacote HD e os sons de terceiros mantêm seus respectivos autores.

Projeto experimental de fã, sem vínculo oficial. Os avisos e créditos dos componentes devem ser preservados. O README histórico da base está em `docs/UPSTREAM-README.md`; detalhes antigos de plataforma e saves nele não substituem as instruções desta versão.

## Estado da validação

O snapshot foi compilado localmente em Windows com as dependências da v0.2.0. A campanha inteira e todas as combinações de máquinas/dependências ainda não foram validadas. A fonte permite estudar e alterar o aplicativo; ela não promete recriar de forma idêntica todos os recursos de terceiros a partir do zero.
