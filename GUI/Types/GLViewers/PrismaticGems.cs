using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using GUI.Utils;
using ValveResourceFormat.ResourceTypes;

namespace GUI.Types.GLViewers
{
    /// <summary>A prismatic gem color, from the "colors" table of items_game.txt.</summary>
    sealed record PrismaticGem(string Name, string LocalName, string Hex, Color Color)
    {
        public bool Matches(string query) => query.Length == 0
            || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || LocalName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Hex.Contains(query.TrimStart('#'), StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Name;

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

            gem = new PrismaticGem(name, localName, hex.ToUpperInvariant(), Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF));
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

    /// <summary>
    /// Draws prismatic gem icons the way the Dota inventory does: the white gem tinted with the gem color,
    /// with the frame and highlights of the mask image over it.
    /// </summary>
    sealed class PrismaticGemIcons : IDisposable
    {
        private const string BaseImage = "panorama/images/econ/sockets/gem_color_png.vtex";
        private const string MaskImage = "panorama/images/econ/sockets/gem_color_mask_png.vtex";

        private readonly Bitmap? baseImage;
        private readonly Bitmap? maskImage;
        private readonly Dictionary<(string Hex, int Size), Bitmap> cache = [];

        public PrismaticGemIcons(VrfGuiContext context)
        {
            baseImage = LoadImage(context, BaseImage);
            maskImage = LoadImage(context, MaskImage);
        }

        public static Bitmap? LoadImage(VrfGuiContext context, string path)
        {
            if (context.LoadFileCompiled(path)?.DataBlock is not Texture texture)
            {
                Log.Warn(nameof(PrismaticGemIcons), $"Missing {path}");
                return null;
            }

            using var bitmap = texture.GenerateBitmap();
            return bitmap.ToBitmap();
        }

        public Bitmap Get(PrismaticGem? gem, int size)
        {
            var key = (gem?.Hex ?? string.Empty, size);
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var icon = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(icon))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, size, size);

                if (baseImage == null)
                {
                    using var brush = new SolidBrush(gem?.Color ?? Color.FromArgb(60, 64, 72));
                    g.FillEllipse(brush, Rectangle.Inflate(rect, -size / 6, -size / 6));
                }
                else if (gem == null)
                {
                    // An empty socket: the gem drained of color
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(TintMatrix(Color.FromArgb(70, 76, 88), 0.55f));
                    g.DrawImage(baseImage, rect, 0, 0, baseImage.Width, baseImage.Height, GraphicsUnit.Pixel, attributes);
                }
                else
                {
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(TintMatrix(gem.Color, 1f));
                    g.DrawImage(baseImage, rect, 0, 0, baseImage.Width, baseImage.Height, GraphicsUnit.Pixel, attributes);
                }

                if (maskImage != null)
                {
                    g.DrawImage(maskImage, rect);
                }
            }

            cache[key] = icon;
            return icon;
        }

        // Multiplies the image by the color, as a tint of a white image
        private static ColorMatrix TintMatrix(Color color, float alpha) => new(
        [
            [color.R / 255f, 0, 0, 0, 0],
            [0, color.G / 255f, 0, 0, 0],
            [0, 0, color.B / 255f, 0, 0],
            [0, 0, 0, alpha, 0],
            [0, 0, 0, 0, 1],
        ]);

        public void Dispose()
        {
            baseImage?.Dispose();
            maskImage?.Dispose();

            foreach (var icon in cache.Values)
            {
                icon.Dispose();
            }

            cache.Clear();
        }
    }
}
