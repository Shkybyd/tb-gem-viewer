using System.Numerics;
using ImGuiNET;
using SkiaSharp;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace TBGemViewer;

/// <summary>
/// The page around the hero: its name on the left, the selected set with its item icons on the right,
/// and the gem list that opens in place of the set.
/// </summary>
sealed class ShowcaseUi
{
    private static readonly Vector4 Text = Rgb(232, 233, 240);
    private static readonly Vector4 Dim = Rgb(150, 148, 168);
    private static readonly Vector4 Arcana = Rgb(173, 229, 92);
    private static readonly Vector4 Tile = Rgb(28, 26, 40);
    private static readonly Vector4 TileHover = Rgb(42, 39, 58);
    private static readonly Vector4 Border = Rgb(58, 54, 76);
    private static readonly Vector4 Drawer = Rgb(17, 15, 27);
    private static readonly Vector4 Green = Rgb(58, 104, 48);
    private static readonly Vector4 GreenHover = Rgb(70, 120, 56);

    private static readonly (string Label, string Activity, bool Loop)[] Actions =
    [
        ("Стойка", "ACT_DOTA_IDLE", true),
        ("Бег", "ACT_DOTA_RUN", true),
        ("Атака", "ACT_DOTA_ATTACK", false),
        ("Reflection", "ACT_DOTA_CAST_ABILITY_1", false),
        ("Metamorphosis", "ACT_DOTA_CAST_ABILITY_3", false),
        ("Sunder", "ACT_DOTA_CAST_ABILITY_4", false),
        ("Насмешка", "ACT_DOTA_TAUNT", false),
        ("Победа", "ACT_DOTA_VICTORY", false),
    ];

    private static readonly (string Image, string Name)[] LoadoutItems =
    [
        ("panorama/images/econ/heroes/terrorblade/arcana_terrorblade_png.vtex", "Fractal Horns of Inner Abysm"),
        ("panorama/images/econ/heroes/terrorblade/horns_arcana_png.vtex", "Рога арканы"),
        ("panorama/images/econ/heroes/terrorblade/armor_png.vtex", "Броня"),
        ("panorama/images/econ/heroes/terrorblade/weapon_png.vtex", "Клинки"),
        ("panorama/images/econ/heroes/terrorblade/wings_png.vtex", "Крылья"),
    ];

    private const string GemBaseImage = "panorama/images/econ/sockets/gem_color_png.vtex";
    private const string GemMaskImage = "panorama/images/econ/sockets/gem_color_mask_png.vtex";

    private readonly GemScene scene;
    private readonly Action lobbyCamera;
    private readonly Action gameCamera;
    private readonly float scale;
    private readonly int[] itemTextures;
    private readonly int gemBase;
    private readonly int gemMask;

    private ImFontPtr titleFont;
    private ImFontPtr bodyFont;
    private ImFontPtr boldFont;
    private ImFontPtr setFont;
    private ImFontPtr gemNameFont;

    private bool drawerOpen;
    private bool lobbyView = true;
    private string search = string.Empty;
    private string hex = string.Empty;
    private PrismaticGem? previousGem;

    /// <summary>Whether the pointer is over the page, so a drag there does not turn the hero.</summary>
    public static bool WantsMouse => ImGui.GetIO().WantCaptureMouse;

    public ShowcaseUi(GameFileLoader files, GemScene scene, Action lobbyCamera, Action gameCamera, float scale)
    {
        this.scene = scene;
        this.lobbyCamera = lobbyCamera;
        this.gameCamera = gameCamera;
        this.scale = scale;

        itemTextures = [.. LoadoutItems.Select(item => LoadImage(files, item.Image))];
        gemBase = LoadImage(files, GemBaseImage);
        gemMask = LoadImage(files, GemMaskImage);
    }

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private float S(float value) => value * scale;

    /// <summary>Loads the fonts from the game: Radiance for the hero name, and Arial, which has Cyrillic, for the rest.</summary>
    public void AddFonts(GameFileLoader files)
    {
        var io = ImGui.GetIO();
        var cyrillic = io.Fonts.GetGlyphRangesCyrillic();

        byte[]? Read(params string[] paths)
        {
            foreach (var path in paths)
            {
                using var stream = files.GetFileStream(path);
                if (stream != null)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }

            return null;
        }

        var regular = Read("vgui/fonts/arial.ttf", "vgui/fonts/tahoma.ttf");
        var bold = Read("vgui/fonts/arialbd.ttf") ?? regular;
        // The small Radiance files hold only a few letters, the regular one has the whole alphabet
        var title = Read("resource/radiance_regular.otf") ?? bold;

        if (regular == null || bold == null || title == null)
        {
            bodyFont = boldFont = setFont = gemNameFont = titleFont = io.Fonts.AddFontDefault();
            return;
        }

        bodyFont = ImGuiRenderer.AddFont(regular, S(17), cyrillic);
        boldFont = ImGuiRenderer.AddFont(bold, S(15), cyrillic);
        setFont = ImGuiRenderer.AddFont(regular, S(25), cyrillic);
        gemNameFont = ImGuiRenderer.AddFont(bold, S(22), cyrillic);
        titleFont = ImGuiRenderer.AddFont(title, S(70), IntPtr.Zero);
    }

    private static int LoadImage(GameFileLoader files, string path)
    {
        if (files.LoadFileCompiled(path)?.DataBlock is not Texture texture)
        {
            return 0;
        }

        using var bitmap = texture.GenerateBitmap();
        using var rgba = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.CopyTo(rgba, SKColorType.Rgba8888);
        return ImGuiRenderer.CreateTexture(rgba.GetPixels(), rgba.Width, rgba.Height);
    }

    public void ApplyStyle()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.FrameRounding = 0;
        style.ScrollbarRounding = 0;
        style.WindowBorderSize = 0;
        style.FrameBorderSize = 1;
        style.ScrollbarSize = S(10);
        style.ItemSpacing = new Vector2(S(6), S(6));
        style.FramePadding = new Vector2(S(10), S(8));
        style.Colors[(int)ImGuiCol.Text] = Text;
        style.Colors[(int)ImGuiCol.TextDisabled] = Dim;
        style.Colors[(int)ImGuiCol.WindowBg] = Drawer;
        style.Colors[(int)ImGuiCol.ChildBg] = Vector4.Zero;
        style.Colors[(int)ImGuiCol.Border] = Border;
        style.Colors[(int)ImGuiCol.FrameBg] = Tile;
        style.Colors[(int)ImGuiCol.FrameBgHovered] = TileHover;
        style.Colors[(int)ImGuiCol.FrameBgActive] = TileHover;
        style.Colors[(int)ImGuiCol.Button] = Tile;
        style.Colors[(int)ImGuiCol.ButtonHovered] = TileHover;
        style.Colors[(int)ImGuiCol.ButtonActive] = Border;
        style.Colors[(int)ImGuiCol.ScrollbarBg] = Vector4.Zero;
        style.Colors[(int)ImGuiCol.ScrollbarGrab] = Border;
        style.Colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(Arcana.X, Arcana.Y, Arcana.Z, 0.35f);
    }

    public void Draw(Vector2 display)
    {
        const ImGuiWindowFlags Overlay = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.PushFont(bodyFont);

        DrawTitle(display, Overlay);

        if (drawerOpen)
        {
            DrawDrawer(display);
        }
        else
        {
            DrawSelected(display, Overlay);
        }

        var hint = "Тяни мышью, чтобы повернуть героя   -   колесо приближает";
        var hintSize = ImGui.CalcTextSize(hint);
        ImGui.GetForegroundDrawList().AddText(new Vector2((display.X - hintSize.X) / 2, display.Y - hintSize.Y - S(18)), ImGui.GetColorU32(Dim), hint);

        ImGui.PopFont();
    }

    private void DrawTitle(Vector2 display, ImGuiWindowFlags flags)
    {
        ImGui.SetNextWindowPos(new Vector2(display.X * 0.045f, display.Y * 0.15f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(S(560), display.X * 0.3f), 0));
        ImGui.Begin("##title", flags | ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.PushFont(titleFont);
        ImGui.TextColored(Text, "TERRORBLADE");
        ImGui.PopFont();

        ImGui.PushFont(setFont);
        ImGui.TextColored(Text, "Fractal Horns of Inner Abysm");
        ImGui.PopFont();
        ImGui.PushFont(boldFont);
        ImGui.TextColored(Arcana, "ARCANA");
        ImGui.PopFont();

        ImGui.Dummy(new Vector2(1, S(6)));
        var gem = scene.Gem;
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var dot = S(12);
        drawList.AddCircleFilled(cursor + new Vector2(dot / 2, ImGui.GetTextLineHeight() / 2), dot / 2, ImGui.GetColorU32(gem?.Color ?? Dim));
        ImGui.SetCursorScreenPos(cursor + new Vector2(dot + S(8), 0));
        ImGui.PushFont(boldFont);
        ImGui.TextColored(gem?.Color ?? Dim, gem == null ? "Без самоцвета" : $"Самоцвет: {gem.Name}");
        ImGui.PopFont();

        ImGui.Dummy(new Vector2(1, S(12)));

        if (FlatButton("Лобби", new Vector2(S(104), S(36)), lobbyView))
        {
            lobbyView = true;
            lobbyCamera();
        }
        ImGui.SameLine();
        if (FlatButton("Как в игре", new Vector2(S(120), S(36)), !lobbyView))
        {
            lobbyView = false;
            gameCamera();
        }
        ImGui.SameLine();
        if (GreenButton("Эффект убийства", new Vector2(S(200), S(36))))
        {
            scene.SpawnKillDemo();
        }

        var rowWidth = 0f;
        var maxWidth = S(440);
        foreach (var (label, activity, loop) in Actions)
        {
            var size = new Vector2(ImGui.CalcTextSize(label.ToUpperInvariant()).X + S(22), S(30));
            if (rowWidth > 0 && rowWidth + size.X < maxWidth)
            {
                ImGui.SameLine();
            }
            else
            {
                rowWidth = 0;
            }

            rowWidth += size.X + S(6);
            if (FlatButton(label, size, false, small: true))
            {
                scene.PlayActivity(activity, loop);
            }
        }

        ImGui.End();
    }

    private void DrawSelected(Vector2 display, ImGuiWindowFlags flags)
    {
        var left = display.X * 0.70f;
        ImGui.SetNextWindowPos(new Vector2(left, display.Y * 0.28f));
        ImGui.SetNextWindowSize(new Vector2(Math.Max(S(320), display.X - left - S(40)), 0));
        ImGui.Begin("##selected", flags | ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.PushFont(boldFont);
        // A small shield before the caption
        var shield = ImGui.GetCursorScreenPos();
        var shieldSize = ImGui.GetTextLineHeight() * 0.8f;
        var shieldTop = shield.Y + ImGui.GetTextLineHeight() * 0.1f;
        ImGui.GetWindowDrawList().AddQuadFilled(new Vector2(shield.X, shieldTop), new Vector2(shield.X + shieldSize, shieldTop),
            new Vector2(shield.X + shieldSize, shieldTop + shieldSize * 0.55f), new Vector2(shield.X, shieldTop + shieldSize * 0.55f), ImGui.GetColorU32(Text));
        ImGui.GetWindowDrawList().AddTriangleFilled(new Vector2(shield.X, shieldTop + shieldSize * 0.55f),
            new Vector2(shield.X + shieldSize, shieldTop + shieldSize * 0.55f), new Vector2(shield.X + shieldSize / 2, shieldTop + shieldSize), ImGui.GetColorU32(Text));
        ImGui.SetCursorScreenPos(shield + new Vector2(shieldSize + S(8), 0));
        ImGui.TextColored(Text, "ВЫБРАНО");
        ImGui.PopFont();
        ImGui.Dummy(new Vector2(1, S(4)));
        ImGui.PushFont(setFont);
        ImGui.TextColored(Text, "Комплект Fractal Horns of Inner Abysm");
        ImGui.PopFont();
        ImGui.PushFont(boldFont);
        ImGui.TextColored(Arcana, "ARCANA");
        ImGui.PopFont();
        ImGui.Dummy(new Vector2(1, S(8)));

        var tile = new Vector2(S(72));
        for (var i = 0; i < LoadoutItems.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, S(12));
            }

            ItemTile($"##item{i}", itemTextures[i], tile, i == 0 ? Arcana : null, LoadoutItems[i].Name);
        }

        ImGui.Dummy(new Vector2(1, S(4)));
        if (GemTileButton("##socket", scene.Gem, tile))
        {
            drawerOpen = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(scene.Gem?.Name ?? "Призматический самоцвет");
        }

        ImGui.Dummy(new Vector2(1, S(20)));
        if (RaisedButton("САМОЦВЕТЫ", new Vector2(S(220), S(54))))
        {
            drawerOpen = true;
        }

        ImGui.End();
    }

    private void DrawDrawer(Vector2 display)
    {
        var width = Math.Min(S(470), display.X / 2);
        ImGui.SetNextWindowPos(new Vector2(display.X - width - S(24), S(24)));
        ImGui.SetNextWindowSize(new Vector2(width, display.Y - S(48)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(S(18), S(16)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.Begin("##drawer", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);

        ImGui.PushFont(boldFont);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("ПРИЗМАТИЧЕСКИЙ САМОЦВЕТ");
        ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - S(104));
        if (FlatButton("Готово", new Vector2(S(104), S(32)), false, small: true))
        {
            drawerOpen = false;
        }
        ImGui.PopFont();

        DrawGemCard(new Vector2(ImGui.GetContentRegionAvail().X, S(118)));

        var half = (ImGui.GetContentRegionAvail().X - S(8)) / 2;
        if (FlatButton("Без гема", new Vector2(half, S(38)), false))
        {
            Select(null);
        }
        ImGui.SameLine(0, S(8));
        if (FlatButton("Сравнить", new Vector2(half, S(38)), false))
        {
            Select(previousGem);
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Поиск: red, gold, синий, FF3C28", ref search, 64);

        var footer = S(48);
        ImGui.BeginChild("##grid", new Vector2(0, -footer));
        var tileSize = new Vector2(S(126), S(130));
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + S(6)) / (tileSize.X + S(6))));
        var shown = 0;

        foreach (var gem in PrismaticGem.All)
        {
            if (!gem.Matches(search.Trim()))
            {
                continue;
            }

            if (shown % columns != 0)
            {
                ImGui.SameLine();
            }

            if (GemListTile(gem, tileSize))
            {
                Select(gem);
            }

            shown++;
        }

        ImGui.EndChild();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - S(126));
        var enter = ImGui.InputTextWithHint("##hex", "Свой цвет: HEX", ref hex, 7, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((FlatButton("Применить", new Vector2(S(120), 0), false) || enter)
            && PrismaticGem.TryParse("Свой цвет", string.Empty, hex, out var custom))
        {
            Select(custom);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void Select(PrismaticGem? gem)
    {
        if (gem == scene.Gem)
        {
            return;
        }

        previousGem = scene.Gem;
        scene.SetGem(gem);
    }

    private void DrawGemCard(Vector2 size)
    {
        var gem = scene.Gem;
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var tint = gem?.Color ?? Dim;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(Tile));
        drawList.AddRectFilledMultiColor(min, max, ImGui.GetColorU32(tint with { W = 0.28f }), 0, 0, ImGui.GetColorU32(tint with { W = 0.28f }));
        drawList.AddRect(min, max, ImGui.GetColorU32(gem == null ? Border : tint with { W = 0.6f }));

        var icon = size.Y - S(16);
        DrawGemIcon(drawList, min + new Vector2(S(10), S(8)), icon, gem);

        var x = min.X + S(10) + icon + S(12);
        ImGui.PushFont(boldFont);
        drawList.AddText(new Vector2(x, min.Y + size.Y * 0.14f), ImGui.GetColorU32(gem == null ? Dim : Arcana), gem == null ? "ОРИГИНАЛЬНЫЙ ЦВЕТ" : "ПРИЗМАТИЧЕСКИЙ");
        ImGui.PopFont();
        ImGui.PushFont(gemNameFont);
        drawList.AddText(new Vector2(x, min.Y + size.Y * 0.32f), ImGui.GetColorU32(Text), gem?.Name ?? "Без самоцвета");
        ImGui.PopFont();
        var sub = gem == null ? "Аркана в стандартных цветах" : gem.LocalName.Length > 0 ? $"#{gem.Hex}  -  {gem.LocalName}" : $"#{gem.Hex}";
        drawList.AddText(new Vector2(x, min.Y + size.Y * 0.62f), ImGui.GetColorU32(Dim), sub);

        ImGui.Dummy(size);
        ImGui.Dummy(new Vector2(1, S(2)));
    }

    /// <summary>Draws the prismatic gem icon as the inventory does: the white gem tinted with the color, the frame over it.</summary>
    private void DrawGemIcon(ImDrawListPtr drawList, Vector2 min, float size, PrismaticGem? gem)
    {
        var max = min + new Vector2(size);
        var color = gem?.Color ?? new Vector4(0.3f, 0.32f, 0.36f, 0.7f);

        if (gemBase != 0)
        {
            drawList.AddImage(gemBase, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(color));
        }
        else
        {
            drawList.AddCircleFilled((min + max) / 2, size / 3, ImGui.GetColorU32(color));
        }

        if (gemMask != 0)
        {
            drawList.AddImage(gemMask, min, max);
        }
    }

    private bool GemListTile(PrismaticGem gem, Vector2 size)
    {
        var selected = gem == scene.Gem;
        var clicked = ImGui.InvisibleButton($"##gem{gem.Hex}{gem.Name}", size);
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered || selected ? TileHover : Tile));

        var icon = size.X * 0.58f;
        var iconMin = min + new Vector2((size.X - icon) / 2, size.Y * 0.08f);
        var center = iconMin + new Vector2(icon / 2);
        for (var ring = 6; ring >= 1; ring--)
        {
            drawList.AddCircleFilled(center, icon * (0.35f + ring * 0.06f), ImGui.GetColorU32(gem.Color with { W = (hovered || selected ? 0.07f : 0.045f) }));
        }

        DrawGemIcon(drawList, iconMin, icon, gem);

        ImGui.PushFont(boldFont);
        var label = gem.Name;
        var wrap = size.X - S(8);
        var labelSize = ImGui.CalcTextSize(label, wrap);
        var textPos = new Vector2(min.X + (size.X - Math.Min(labelSize.X, wrap)) / 2, iconMin.Y + icon + S(4));
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), textPos, ImGui.GetColorU32(selected ? Arcana : Text), label, wrap);
        ImGui.PopFont();

        drawList.AddRect(min, max, ImGui.GetColorU32(selected ? Arcana : hovered ? Border : Rgb(40, 37, 54)), 0, ImDrawFlags.None, selected ? 2 : 1);
        return clicked;
    }

    private static void ItemTile(string id, int texture, Vector2 size, Vector4? accent, string tooltip)
    {
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.47f, 0.47f, 0.63f, hovered ? 0.28f : 0.16f)));
        if (texture != 0)
        {
            drawList.AddImage(texture, min + new Vector2(2), max - new Vector2(2));
        }

        DrawTileFrame(drawList, min, max, accent, hovered);

        if (hovered)
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private bool GemTileButton(string id, PrismaticGem? gem, Vector2 size)
    {
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.47f, 0.47f, 0.63f, hovered ? 0.28f : 0.16f)));
        DrawGemIcon(drawList, min + new Vector2(4), size.X - 8, gem);
        DrawTileFrame(drawList, min, max, gem?.Color, hovered);
        return clicked;
    }

    private static void DrawTileFrame(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4? accent, bool hovered)
    {
        var border = accent ?? new Vector4(0.75f, 0.76f, 0.84f, hovered ? 0.8f : 0.45f);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), 0, ImDrawFlags.None, accent != null ? 2 : 1);

        if (accent != null)
        {
            // A cut corner in the accent color
            drawList.AddTriangleFilled(new Vector2(max.X - 12, min.Y), new Vector2(max.X, min.Y), new Vector2(max.X, min.Y + 12), ImGui.GetColorU32(accent.Value));
        }
    }

    private bool FlatButton(string label, Vector2 size, bool toggled, bool small = false)
    {
        ImGui.PushFont(small ? bodyFont : boldFont);
        ImGui.PushStyleColor(ImGuiCol.Text, toggled ? Arcana : Text);
        ImGui.PushStyleColor(ImGuiCol.Button, toggled ? Green with { W = 0.55f } : Tile);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, toggled ? GreenHover : TileHover);
        ImGui.PushStyleColor(ImGuiCol.Border, toggled ? Arcana with { W = 0.6f } : Border);
        var clicked = ImGui.Button(label.ToUpperInvariant(), size);
        ImGui.PopStyleColor(4);
        ImGui.PopFont();
        return clicked;
    }

    private bool GreenButton(string label, Vector2 size)
    {
        ImGui.PushFont(boldFont);
        ImGui.PushStyleColor(ImGuiCol.Button, Green);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GreenHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(52, 92, 44));
        ImGui.PushStyleColor(ImGuiCol.Border, Rgb(120, 170, 100));
        var clicked = ImGui.Button(label.ToUpperInvariant(), size);
        ImGui.PopStyleColor(4);
        ImGui.PopFont();
        return clicked;
    }

    private bool RaisedButton(string label, Vector2 size)
    {
        var clicked = ImGui.InvisibleButton("##raised" + label, size);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        var top = held ? Rgb(78, 86, 100) : hovered ? Rgb(112, 122, 138) : Rgb(96, 105, 120);
        var bottom = held ? Rgb(58, 64, 76) : hovered ? Rgb(82, 90, 104) : Rgb(68, 75, 88);
        drawList.AddRectFilledMultiColor(min, max, ImGui.GetColorU32(top), ImGui.GetColorU32(top), ImGui.GetColorU32(bottom), ImGui.GetColorU32(bottom));
        drawList.AddLine(min, new Vector2(max.X, min.Y), ImGui.GetColorU32(Rgb(150, 160, 175)));

        ImGui.PushFont(gemNameFont);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(min + (size - textSize) / 2, ImGui.GetColorU32(Text), label);
        ImGui.PopFont();
        return clicked;
    }
}
