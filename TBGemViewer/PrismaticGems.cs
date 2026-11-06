using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace TBGemViewer;

/// <summary>A prismatic gem color, from the "colors" table of items_game.txt.</summary>
sealed record PrismaticGem(string Name, string LocalName, string Hex, byte R, byte G, byte B)
{
    /// <summary>The color with each channel from 0 to 1, as ImGui takes it.</summary>
    public Vector4 Color => new(R / 255f, G / 255f, B / 255f, 1f);

    public bool Matches(string query) => query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || LocalName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Hex.Contains(query.TrimStart('#'), StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string name, string localName, string hex, [NotNullWhen(true)] out PrismaticGem? gem)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(static c => $"{c}{c}"));
        }

        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            gem = null;
            return false;
        }

        gem = new PrismaticGem(name, localName, hex.ToUpperInvariant(), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static PrismaticGem Make(string name, string localName, string hex)
        => TryParse(name, localName, hex, out var gem) ? gem : throw new ArgumentException(hex);

    public static readonly PrismaticGem[] All =
    [
        Make("Red", "Красный", "D03D33"),
        Make("Rubiline", "", "D11FA1"),
        Make("Gold", "Золотой", "CFAB31"),
        Make("Blue", "Синий", "0097CE"),
        Make("Purple", "Фиолетовый", "8232CF"),
        Make("Orange", "Оранжевый", "D07733"),
        Make("Light Green", "Светло-зелёный", "B7CF33"),
        Make("Deep Blue", "Глубокий синий", "3D68C4"),
        Make("Sea Green", "Сине-зелёный", "4AB78D"),
        Make("Verdant Green", "Зеленая сосна", "51B350"),
        Make("Deep Green", "Глубокий зелёный", "37864D"),
        Make("Bright Purple", "Ярко-фиолетовый", "8232ED"),
        Make("Bright Green", "Ярко-зелёный", "A1FF59"),
        Make("Placid Blue", "Спокойный синий", "94CAD0"),
        Make("Summer Warmth", "Летнее тепло", "FFEEBC"),
        Make("Defensive Red", "Сдержанно-красный", "FF4200"),
        Make("Creator's Light", "Свет создателя", "DCF2FF"),
        Make("Blossom Red", "Аленький цветочек", "D76092"),
        Make("Crystalline Blue", "Синий кристалл", "1A3D85"),
        Make("Cursed Black", "Отталкивающий чёрный", "060606"),
        Make("Plague Grey", "Чумной серый", "626E5B"),
        Make("Champion's Blue", "Чемпионский синий", "507DFE"),
        Make("Champion's Green", "Чемпионский зелёный", "15A515"),
        Make("Midas Gold", "Золото Hand of Midas", "FFCA15"),
        Make("Earth Green", "Зеленая планета", "5AC355"),
        Make("Ember Flame", "Тлеющее пламя", "FFC604"),
        Make("Diretide Orange", "", "F79D00"),
        Make("Dredge Earth", "", "BDB76B"),
        Make("Dungeon Doom", "", "7B68EE"),
        Make("Tnim S'nnam", "", "BCDDB3"),
        Make("Brusque Britches Beige", "", "F0E68C"),
        Make("Unhallowed Ground", "", "808000"),
        Make("Ships in the Night", "", "191970"),
        Make("Miasmatic Grey", "", "C0C0C0"),
        Make("Pristine Platinum", "", "D5E3F5"),
        Make("Vermillion Renewal", "", "CA0123"),
        Make("Reflection's Shade", "", "FF3C28"),
        Make("Pyroclastic Flow", "", "FF7832"),
        Make("Glacial Flow", "", "32ABDC"),
        Make("Plushy Shag", "", "FFC1DC"),
        Make("Champion's Purple", "Чемпионский фиолетовый", "7F48C3"),
        Make("Explosive Burst", "", "FFAF00"),
    ];
}
