<h1 align="center">TB Gem Viewer</h1>

<p align="center">
  Preview <b>Fractal Horns of Inner Abysm</b>, the Terrorblade arcana, with any prismatic gem before you buy one.
  <br />
  Просмотр арканы Террорблейда <b>Fractal Horns of Inner Abysm</b> с любым призматическим самоцветом до покупки.
  <br />
  Windows · Linux (Arch)
</p>

## Русский

В Dota нельзя примерить призматический самоцвет, которого у тебя нет, а по скриншотам с
торговой площадки сложно понять, как цвет будет выглядеть на аркане. TB Gem Viewer рисует
Террорблейда в аркане прямо из файлов твоей установки Dota 2 и красит эффекты гемом так же,
как это делает игра.

**Что умеет**

- Аркана целиком: модель, рога, броня, крылья, клинки, аура, глаза, огонь на клинках и ногах.
- Все 42 призматических самоцвета с точными цветами из файлов игры, с поиском по английскому и русскому названию или hex.
- Любой свой цвет в формате hex и «Сравнить», чтобы переключаться между двумя последними гемами.
- Экран в стиле страницы героя: имя слева, блок «Выбрано» с иконками предметов справа, кнопка «Самоцветы» открывает список гемов.
- Ракурсы «Лобби» и «Как в игре», анимации и эффект убийства арканы.
- Управление как на странице героя: тяни мышью влево-вправо, чтобы повернуть героя, колесо приближает.

**Что нужно**

- Dota 2, установленная через Steam. Программа только читает её файлы и ничего в них не меняет, VAC это не касается.
- Видеокарта с OpenGL 4.6: на Linux это Mesa (AMD, Intel) или драйвер NVIDIA.

**Windows.** Скачай `TBGemViewer-windows-x64.exe` из [Releases](../../releases) и запусти. Устанавливать .NET не нужно.

**Arch Linux.** Собери пакет из `packaging/arch`:

```sh
git clone https://github.com/Shkybyd/tb-gem-viewer.git
cd tb-gem-viewer/packaging/arch
makepkg -si
tb-gem-viewer
```

Или скачай `tb-gem-viewer-linux-x64.tar.gz` из [Releases](../../releases), распакуй и запусти `./TBGemViewer`.
Steam ищется в `~/.steam`, `~/.local/share/Steam`, во Flatpak и Snap. Если Dota стоит в другом месте,
передай путь к пакету: `tb-gem-viewer "/путь/dota 2 beta/game/dota/pak01_dir.vpk"`.

## English

Dota does not let you preview a prismatic gem you do not own, and market screenshots rarely
show how a color looks on the arcana. TB Gem Viewer renders Terrorblade with the arcana from
your own Dota 2 install and tints the effects with the gem the same way the game does.

**Features**

- The whole arcana: model, horns, armor, wings, blades, aura, eyes, and the fire on blades and feet.
- All 42 prismatic gems with their exact colors from the game files, searchable by English or Russian name or hex.
- Any custom hex color, and "Compare" to swap between the last two gems.
- A hero page layout: the name on the left, the selected set with its item icons on the right, and a "Gems" button that opens the gem list.
- Lobby and in-game camera angles, animations, and the arcana kill effect.
- Hero page controls: drag left or right to turn the hero, scroll to zoom.

**Requirements**

- Dota 2 installed through Steam. The app only reads its files and changes nothing, so VAC is not involved.
- A GPU with OpenGL 4.6: on Linux, Mesa (AMD, Intel) or the NVIDIA driver.

**Windows.** Download `TBGemViewer-windows-x64.exe` from [Releases](../../releases) and run it. No .NET install is needed.

**Arch Linux.** Build the package in `packaging/arch` with `makepkg -si` and run `tb-gem-viewer`, or download
`tb-gem-viewer-linux-x64.tar.gz` from [Releases](../../releases), unpack it and run `./TBGemViewer`.
Steam is looked up in `~/.steam`, `~/.local/share/Steam`, Flatpak and Snap. If Dota is elsewhere, pass
its package path as the argument.

## How it works

The particles of the arcana and of the default items tint themselves with `C_INIT_RemapCPtoVector`:
control point 15 holds the gem color as 0–255 RGB and the x of control point 16 turns the tint on.
The viewer sets them as the game does. Gem colors come from the `colors` table of
`scripts/items/items_game.txt`, item icons and fonts from the game files as well.

The app in `TBGemViewer/` is a GLFW window with the scene renderer of Source 2 Viewer and a page drawn
with Dear ImGui, so the same code runs on Windows and Linux:

- `GemScene.cs`: the hero, its default items, the effects and the gem tint.
- `ShowcaseUi.cs`: the page around the hero.
- `ImGuiRenderer.cs`: ImGui input and drawing over the frame.

The fork also keeps the gem showcase inside the Windows-only Source 2 Viewer GUI (`Source2Viewer.exe --tbgem`).

## Building

```sh
# Windows
dotnet publish TBGemViewer/TBGemViewer.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# Linux
dotnet publish TBGemViewer/TBGemViewer.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=false -p:PublishReadyToRun=false
```

Needs the .NET 10 SDK.

## Credits and license

TB Gem Viewer is a fork of [Source 2 Viewer (ValveResourceFormat)](https://github.com/ValveResourceFormat/ValveResourceFormat),
which does all the reading and rendering of Source 2 resources. Its original readme is in
[README.SOURCE2VIEWER.md](./README.SOURCE2VIEWER.md). Released under the MIT license, see [LICENSE](./LICENSE).

Not affiliated with or endorsed by Valve. Dota 2, Terrorblade and all game assets belong to
Valve Corporation. No game files are included: models, effects, icons and fonts are all loaded
from your own installation.

Known limitation: the glow planes on the default blades (`terrorblade_ambient_sword_blade`)
are not drawn, because the renderer places that model off the blades. The fire on the blades
still shows the gem color.
