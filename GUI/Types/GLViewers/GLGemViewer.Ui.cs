using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using ValveResourceFormat.Renderer;

namespace GUI.Types.GLViewers
{
    sealed partial class GLGemViewer
    {
        /// <summary>Colors of the hero page: violet shadows, cold grey text and the arcana rarity green.</summary>
        private static class Palette
        {
            public static readonly Color Drawer = Color.FromArgb(17, 15, 27);
            public static readonly Color DrawerLight = Color.FromArgb(27, 24, 41);
            public static readonly Color Tile = Color.FromArgb(28, 26, 40);
            public static readonly Color TileHover = Color.FromArgb(42, 39, 58);
            public static readonly Color Border = Color.FromArgb(58, 54, 76);
            public static readonly Color Text = Color.FromArgb(232, 233, 240);
            public static readonly Color Dim = Color.FromArgb(150, 148, 168);
            public static readonly Color Arcana = Color.FromArgb(173, 229, 92);
        }

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

        /// <summary>Inventory icons of what the arcana loadout wears.</summary>
        private static readonly (string Image, string Name)[] LoadoutItems =
        [
            ("panorama/images/econ/heroes/terrorblade/arcana_terrorblade_png.vtex", "Fractal Horns of Inner Abysm"),
            ("panorama/images/econ/heroes/terrorblade/horns_arcana_png.vtex", "Рога арканы"),
            ("panorama/images/econ/heroes/terrorblade/armor_png.vtex", "Броня"),
            ("panorama/images/econ/heroes/terrorblade/weapon_png.vtex", "Клинки"),
            ("panorama/images/econ/heroes/terrorblade/wings_png.vtex", "Крылья"),
        ];

        private PrismaticGemIcons? gemIcons;
        private Icon? windowIcon;
        private readonly List<Bitmap> itemImages = [];
        private readonly List<GemTile> gemTiles = [];
        private readonly StageBackdrop backdrop = new();
        private System.Windows.Forms.Timer? backdropTimer;
        private Size sampledViewport;

        private TitleBlock? titleBlock;
        private OverlayPanel? actionsBlock;
        private SelectedBlock? selectedBlock;
        private DrawerPanel? gemDrawer;
        private GemCard? gemCard;
        private TextBox? searchBox;
        private ShowcaseButton? lobbyButton;
        private ShowcaseButton? gameButton;

        private int Dpi(int value) => UiControl!.AdjustForDPI(value);

        protected override void AddUiControls()
        {
            base.AddUiControls();
            Debug.Assert(UiControl != null);

            // The showcase replaces the sidebar and the toolbar of the model viewer
            UiControl.HideSidebar();
            var viewport = UiControl.GLControlContainer;
            var host = viewport.Parent!;

            foreach (Control control in host.Controls)
            {
                if (control != viewport)
                {
                    control.Visible = false;
                }
            }

            gemIcons = new PrismaticGemIcons(GuiContext);

            foreach (var (image, _) in LoadoutItems)
            {
                itemImages.Add(PrismaticGemIcons.LoadImage(GuiContext, image) ?? new Bitmap(1, 1));
            }

            titleBlock = new TitleBlock(backdrop);
            actionsBlock = BuildActions();
            selectedBlock = BuildSelected();
            gemDrawer = BuildDrawer();
            gemDrawer.Visible = false;

            // Floating over the render, painted with the stage behind them so they seem to stand on it
            viewport.Controls.Add(titleBlock);
            viewport.Controls.Add(actionsBlock);
            viewport.Controls.Add(selectedBlock);
            viewport.Controls.Add(gemDrawer);
            titleBlock.BringToFront();
            actionsBlock.BringToFront();
            selectedBlock.BringToFront();
            gemDrawer.BringToFront();
            viewport.Resize += (_, _) => LayoutOverlay();
            LayoutOverlay();

            backdropTimer = new System.Windows.Forms.Timer { Interval = 400 };
            backdropTimer.Tick += (_, _) => SampleBackdrop();
            backdropTimer.Start();

            OnGemChanged();
            ApplyView();
            ShowFpsOverlay = false;

            // The window carries a gem instead of the viewer logo
            if (PrismaticGem.All.FirstOrDefault(static g => g.Name == "Reflection's Shade") is { } iconGem && Program.MainForm != null)
            {
                windowIcon = Icon.FromHandle(gemIcons.Get(iconGem, 64).GetHicon());
                Program.MainForm.Icon = windowIcon;
                Program.MainForm.SetTitleBarLogo(new Bitmap(gemIcons.Get(iconGem, Dpi(28))));
            }
        }

        /// <summary>Places the blocks around the hero, relative to the size of the view.</summary>
        private void LayoutOverlay()
        {
            var viewport = UiControl!.GLControlContainer;
            var (w, h) = (viewport.ClientSize.Width, viewport.ClientSize.Height);
            if (w <= 0 || h <= 0 || titleBlock == null)
            {
                return;
            }

            // The stage lights only the middle of the view, the blocks stay on the flat sides of it
            var left = (int)(w * 0.045f);
            var sideWidth = (int)(w * 0.30f) - left;
            titleBlock.Bounds = new Rectangle(left, (int)(h * 0.15f), Math.Min(Dpi(600), sideWidth), Dpi(170));
            actionsBlock!.Bounds = new Rectangle(left, titleBlock.Bottom + Dpi(12), Math.Min(Dpi(470), sideWidth), Dpi(140));

            var selectedLeft = (int)(w * 0.70f);
            selectedBlock!.Bounds = new Rectangle(selectedLeft, (int)(h * 0.28f), Math.Max(Dpi(300), w - selectedLeft - Dpi(40)), Dpi(340));

            var drawerWidth = Math.Min(Dpi(470), w / 2);
            gemDrawer!.Bounds = new Rectangle(w - drawerWidth - Dpi(24), Dpi(24), drawerWidth, h - Dpi(48));
        }

        /// <summary>
        /// Reads the stage at the right edge of the view, where nothing stands in front of it, so the
        /// floating blocks can paint the same color row by row.
        /// </summary>
        private void SampleBackdrop()
        {
            var viewport = UiControl!.GLControlContainer;
            if (backdrop.IsReady && sampledViewport == viewport.ClientSize)
            {
                return;
            }

            using var frame = ReadWindowPixels();
            if (frame == null || frame.Width < 4 || frame.Height < 4)
            {
                return;
            }

            var rows = new Color[frame.Height];
            for (var y = 0; y < frame.Height; y++)
            {
                var pixel = frame.GetPixel(frame.Width - 2, y);
                rows[y] = Color.FromArgb(pixel.Red, pixel.Green, pixel.Blue);
            }

            // A frame from before the stage was drawn is black all the way down
            if (rows.All(static c => c.R + c.G + c.B < 6))
            {
                return;
            }

            backdrop.Update(rows, viewport);
            sampledViewport = viewport.ClientSize;

            foreach (Control control in viewport.Controls)
            {
                control.Invalidate(true);
            }
        }

        private OverlayPanel BuildActions()
        {
            var panel = new OverlayPanel(backdrop);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
            var backdropFlow = new BackdropFlow(backdrop, flow);

            lobbyButton = new ShowcaseButton("Лобби", LobbyCamera) { Toggled = true };
            gameButton = new ShowcaseButton("Как в игре", GameCamera);
            lobbyButton.Click += (_, _) => SetViewToggle(lobbyView: true);
            gameButton.Click += (_, _) => SetViewToggle(lobbyView: false);
            flow.Controls.Add(Sized(lobbyButton, 104));
            flow.Controls.Add(Sized(gameButton, 120));
            flow.Controls.Add(Sized(new ShowcaseButton("Эффект убийства ↗", SpawnKillDemo) { Primary = true }, 200));

            foreach (var (label, activity, loop) in Actions)
            {
                flow.Controls.Add(Sized(new ShowcaseButton(label, () => PlayActivity(activity, loop)) { Small = true }, label.Length > 9 ? 124 : 88, 30));
            }

            panel.Controls.Add(backdropFlow);
            return panel;

            Control Sized(Control control, int width, int height = 36)
            {
                control.Size = new Size(Dpi(width), Dpi(height));
                control.Margin = new Padding(0, 0, Dpi(6), Dpi(6));
                return control;
            }
        }

        private SelectedBlock BuildSelected()
        {
            var block = new SelectedBlock(backdrop, Dpi);
            var tileSize = Dpi(72);

            for (var i = 0; i < LoadoutItems.Length; i++)
            {
                block.AddTile(new ItemTile(backdrop, itemImages[i], LoadoutItems[i].Name, i == 0) { Size = new Size(tileSize, tileSize) });
            }

            var gemTile = new ItemTile(backdrop, null, "Призматический самоцвет", false, () => gemIcons!.Get(currentGem, tileSize), () => currentGem?.Color)
            {
                Size = new Size(tileSize, tileSize),
                Cursor = Cursors.Hand,
            };
            gemTile.Click += (_, _) => OpenDrawer();
            block.AddTile(gemTile, newRow: true);

            block.SetButton(new ShowcaseButton("Самоцветы", OpenDrawer) { Raised = true, Size = new Size(Dpi(220), Dpi(54)) });
            return block;
        }

        private DrawerPanel BuildDrawer()
        {
            var panel = new DrawerPanel { Padding = new Padding(Dpi(18), Dpi(16), Dpi(14), Dpi(16)) };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Palette.Drawer,
                Margin = Padding.Empty,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var height in (int[])[40, 128, 50, 46, -1, 48])
            {
                layout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, Dpi(height)));
            }

            var header = new UnstyledPanel { Dock = DockStyle.Fill, BackColor = Palette.Drawer, Margin = Padding.Empty };
            header.Controls.Add(new CaptionLabel("ПРИЗМАТИЧЕСКИЙ САМОЦВЕТ") { Dock = DockStyle.Fill });
            header.Controls.Add(new ShowcaseButton("Готово", CloseDrawer) { Dock = DockStyle.Right, Width = Dpi(104), Small = true });
            layout.Controls.Add(header, 0, 0);

            gemCard = new GemCard(gemIcons!) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Dpi(10)) };
            layout.Controls.Add(gemCard, 0, 1);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, Dpi(10)), BackColor = Palette.Drawer };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.Controls.Add(new ShowcaseButton("Без гема", () => SelectGem(null)) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Dpi(4), 0) }, 0, 0);
            buttons.Controls.Add(new ShowcaseButton("⇄ Сравнить", () => SelectGem(previousGem)) { Dock = DockStyle.Fill, Margin = new Padding(Dpi(4), 0, 0, 0) }, 1, 0);
            layout.Controls.Add(buttons, 0, 2);

            searchBox = new TextBox { PlaceholderText = "Поиск: red, gold, синий, FF3C28…" };
            searchBox.TextChanged += (_, _) => FilterTiles();
            layout.Controls.Add(new FieldBox(searchBox) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Dpi(10)) }, 0, 3);

            var grid = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                BackColor = Palette.Drawer,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };

            var tileSize = new Size(Dpi(126), Dpi(130));
            foreach (var gem in PrismaticGem.All)
            {
                var tile = new GemTile(gem, gemIcons!) { Size = tileSize, Margin = new Padding(Dpi(3)) };
                tile.Click += (_, _) => SelectGem(tile.Gem);
                gemTiles.Add(tile);
                grid.Controls.Add(tile);
            }
            layout.Controls.Add(grid, 0, 4);

            var hexBox = new TextBox { PlaceholderText = "Свой цвет: HEX", MaxLength = 7 };
            void ApplyHex()
            {
                if (PrismaticGem.TryParse("Свой цвет", string.Empty, hexBox.Text, out var gem))
                {
                    SelectGem(gem);
                }
            }
            hexBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ApplyHex();
                }
            };
            var hexRow = new UnstyledPanel { Dock = DockStyle.Fill, Margin = new Padding(0, Dpi(10), 0, 0), BackColor = Palette.Drawer };
            hexRow.Controls.Add(new FieldBox(hexBox) { Dock = DockStyle.Fill });
            hexRow.Controls.Add(new Spacer(Dpi(6)) { Dock = DockStyle.Right, BackColor = Palette.Drawer });
            hexRow.Controls.Add(new ShowcaseButton("Применить", ApplyHex) { Dock = DockStyle.Right, Width = Dpi(120) });
            layout.Controls.Add(hexRow, 0, 5);

            panel.Controls.Add(layout);
            return panel;
        }

        // The gem list takes the place of the selected items while it is open
        private void OpenDrawer()
        {
            selectedBlock!.Visible = false;
            gemDrawer!.Visible = true;
            gemDrawer.BringToFront();
        }

        private void CloseDrawer()
        {
            gemDrawer!.Visible = false;
            selectedBlock!.Visible = true;
        }

        private void SetViewToggle(bool lobbyView)
        {
            lobbyButton!.Toggled = lobbyView;
            gameButton!.Toggled = !lobbyView;
        }

        private void FilterTiles()
        {
            var query = searchBox?.Text.Trim() ?? string.Empty;
            var grid = gemTiles.FirstOrDefault()?.Parent;
            grid?.SuspendLayout();

            foreach (var tile in gemTiles)
            {
                tile.Visible = tile.Gem.Matches(query);
            }

            grid?.ResumeLayout();
        }

        private void OnGemChanged()
        {
            if (gemCard == null)
            {
                return;
            }

            gemCard.Gem = currentGem;
            titleBlock!.Gem = currentGem;
            selectedBlock!.Invalidate(true);

            foreach (var tile in gemTiles)
            {
                tile.Selected = tile.Gem == currentGem;
            }
        }

        protected override void RenderOverlayLines(Scene.RenderContext renderContext)
        {
            base.RenderOverlayLines(renderContext);

            if (MainFramebuffer != null)
            {
                TextRenderer.AddText(new ValveResourceFormat.Renderer.TextRenderer.TextRenderRequest
                {
                    X = MainFramebuffer.Width / 2f,
                    Y = MainFramebuffer.Height - Dpi(20),
                    Scale = Dpi(13),
                    Color = new Color32(Palette.Dim.R, Palette.Dim.G, Palette.Dim.B),
                    Text = "DRAG TO TURN THE HERO   -   SCROLL TO ZOOM",
                    CenterHorizontal = true,
                });
            }
        }

        public override void Dispose()
        {
            backdropTimer?.Dispose();
            base.Dispose();
            titleBlock?.Dispose();
            actionsBlock?.Dispose();
            selectedBlock?.Dispose();
            gemDrawer?.Dispose();
            gemCard?.Dispose();
            searchBox?.Dispose();
            lobbyButton?.Dispose();
            gameButton?.Dispose();
            gemIcons?.Dispose();
            windowIcon?.Dispose();

            foreach (var image in itemImages)
            {
                image.Dispose();
            }
        }

        private static Font UiFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style, GraphicsUnit.Point);

        /// <summary>The color of the stage row by row, read from the edge of the render.</summary>
        private sealed class StageBackdrop
        {
            private Color[] rows = [];
            private Control? viewport;

            public bool IsReady => rows.Length > 0;

            public void Update(Color[] sampledRows, Control renderArea)
            {
                rows = sampledRows;
                viewport = renderArea;
            }

            /// <summary>Paints the stage that lies behind a control over the render.</summary>
            public void Paint(Graphics graphics, Control control)
            {
                var rect = control.ClientRectangle;
                if (rows.Length == 0 || viewport == null || !viewport.IsHandleCreated || !control.IsHandleCreated)
                {
                    using var fallback = new SolidBrush(Color.FromArgb(14, 12, 24));
                    graphics.FillRectangle(fallback, rect);
                    return;
                }

                var top = viewport.PointToClient(control.PointToScreen(Point.Empty)).Y;
                for (var y = 0; y < rect.Height; y++)
                {
                    var row = Math.Clamp((top + y) * rows.Length / Math.Max(1, viewport.ClientSize.Height), 0, rows.Length - 1);
                    using var pen = new Pen(rows[row]);
                    graphics.DrawLine(pen, 0, y, rect.Width, y);
                }
            }
        }

        /// <summary>A control painted with the stage behind it, so it seems to float over the render.</summary>
        private class OverlayControl : Control
        {
            protected StageBackdrop Backdrop { get; }

            public OverlayControl(StageBackdrop backdrop)
            {
                Backdrop = backdrop;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs pevent) => Backdrop.Paint(pevent.Graphics, this);
        }

        private sealed class OverlayPanel(StageBackdrop backdrop) : OverlayControl(backdrop);

        /// <summary>A flow panel whose gaps show the stage, holding buttons that are boxes of their own.</summary>
        private sealed class BackdropFlow : OverlayControl
        {
            public BackdropFlow(StageBackdrop backdrop, FlowLayoutPanel flow) : base(backdrop)
            {
                Dock = DockStyle.Fill;
                flow.Paint += (_, e) => Backdrop.Paint(e.Graphics, flow);
                Controls.Add(flow);
            }
        }

        /// <summary>The hero name in large letters with the arcana and the equipped gem under it.</summary>
        private sealed class TitleBlock : OverlayControl
        {
            private readonly Font nameFont = UiFont(44f, FontStyle.Bold);
            private readonly Font setFont = UiFont(14f);
            private readonly Font tagFont = UiFont(9f, FontStyle.Bold);
            private readonly Font gemFont = UiFont(11f, FontStyle.Bold);
            private PrismaticGem? gem;

            public PrismaticGem? Gem
            {
                get => gem;
                set
                {
                    gem = value;
                    Invalidate();
                }
            }

            public TitleBlock(StageBackdrop backdrop) : base(backdrop)
            {
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                var nameSize = System.Windows.Forms.TextRenderer.MeasureText(g, "TERRORBLADE", nameFont, Size, flags);
                System.Windows.Forms.TextRenderer.DrawText(g, "TERRORBLADE", nameFont, new Rectangle(0, 0, Width, nameSize.Height), Palette.Text, flags);

                var y = nameSize.Height + 4;
                var setHeight = setFont.Height;
                System.Windows.Forms.TextRenderer.DrawText(g, "Fractal Horns of Inner Abysm", setFont, new Rectangle(2, y, Width, setHeight), Palette.Text, flags);
                y += setHeight + 2;
                System.Windows.Forms.TextRenderer.DrawText(g, "ARCANA", tagFont, new Rectangle(2, y, Width, tagFont.Height), Palette.Arcana, flags);
                y += tagFont.Height + 12;

                var dot = gemFont.Height - 4;
                using (var brush = new SolidBrush(gem?.Color ?? Palette.Dim))
                {
                    g.FillEllipse(brush, 3, y + 2, dot, dot);
                }
                System.Windows.Forms.TextRenderer.DrawText(g, gem == null ? "Без самоцвета" : $"Самоцвет: {gem.Name}", gemFont,
                    new Rectangle(dot + 10, y, Width - dot - 10, gemFont.Height + 2), gem?.Color ?? Palette.Dim, flags);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    nameFont.Dispose();
                    setFont.Dispose();
                    tagFont.Dispose();
                    gemFont.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>The selected set: its name, its item icons and the button that opens the gem list.</summary>
        private sealed class SelectedBlock : OverlayControl
        {
            private readonly Func<int, int> dpi;
            private readonly List<(Control Tile, bool NewRow)> tiles = [];
            private ShowcaseButton? button;
            private readonly Font headerFont = UiFont(11f, FontStyle.Bold);
            private readonly Font setFont = UiFont(17f);
            private readonly Font tagFont = UiFont(9f, FontStyle.Bold);

            public SelectedBlock(StageBackdrop backdrop, Func<int, int> dpi) : base(backdrop)
            {
                this.dpi = dpi;
            }

            public void AddTile(Control tile, bool newRow = false)
            {
                tiles.Add((tile, newRow));
                Controls.Add(tile);
                PerformLayout();
            }

            public void SetButton(ShowcaseButton showcaseButton)
            {
                button = showcaseButton;
                Controls.Add(showcaseButton);
                PerformLayout();
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);

                var gap = dpi(12);
                var x = 0;
                var y = dpi(112);
                var rowHeight = 0;

                foreach (var (tile, newRow) in tiles)
                {
                    if (newRow || x + tile.Width > Width)
                    {
                        x = 0;
                        y += rowHeight + gap;
                    }

                    tile.Location = new Point(x, y);
                    x += tile.Width + gap;
                    rowHeight = Math.Max(rowHeight, tile.Height);
                }

                button?.Location = new Point(0, y + rowHeight + dpi(26));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

                // A small shield before the caption
                var icon = headerFont.Height - 6;
                using (var shield = new GraphicsPath())
                {
                    shield.AddPolygon([new Point(0, 3), new Point(icon, 3), new Point(icon, icon / 2 + 3), new Point(icon / 2, icon + 3), new Point(0, icon / 2 + 3)]);
                    using var brush = new SolidBrush(Palette.Text);
                    g.FillPath(brush, shield);
                }

                System.Windows.Forms.TextRenderer.DrawText(g, "ВЫБРАНО", headerFont, new Rectangle(icon + 10, 0, Width, headerFont.Height), Palette.Text, flags);
                var y = headerFont.Height + dpi(14);
                System.Windows.Forms.TextRenderer.DrawText(g, "Комплект Fractal Horns of Inner Abysm", setFont, new Rectangle(0, y, Width, setFont.Height + 4), Palette.Text, flags);
                y += setFont.Height + 2;
                System.Windows.Forms.TextRenderer.DrawText(g, "ARCANA", tagFont, new Rectangle(1, y, Width, tagFont.Height), Palette.Arcana, flags);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    headerFont.Dispose();
                    setFont.Dispose();
                    tagFont.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>An inventory icon of the loadout, framed in grey, or in the rarity or gem color.</summary>
        private sealed class ItemTile : OverlayControl
        {
            private readonly Image? image;
            private readonly Func<Image>? imageProvider;
            private readonly Func<Color?>? accentProvider;
            private readonly bool arcana;
            private readonly ToolTip toolTip = new();
            private bool hover;

            public ItemTile(StageBackdrop backdrop, Image? image, string name, bool arcana, Func<Image>? imageProvider = null, Func<Color?>? accentProvider = null)
                : base(backdrop)
            {
                this.image = image;
                this.imageProvider = imageProvider;
                this.accentProvider = accentProvider;
                this.arcana = arcana;
                toolTip.SetToolTip(this, name);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var rect = ClientRectangle;

                using (var fill = new SolidBrush(Color.FromArgb(hover ? 70 : 40, 120, 120, 160)))
                {
                    g.FillRectangle(fill, rect);
                }

                var picture = imageProvider?.Invoke() ?? image;
                if (picture != null)
                {
                    g.DrawImage(picture, Rectangle.Inflate(rect, -2, -2));
                }

                var accent = accentProvider?.Invoke() ?? (arcana ? Palette.Arcana : null);
                using var border = new Pen(accent ?? Color.FromArgb(hover ? 200 : 120, 190, 195, 215), accent != null ? 2f : 1f);
                g.DrawRectangle(border, 1, 1, rect.Width - 2, rect.Height - 2);

                if (accent != null)
                {
                    using var corner = new SolidBrush(accent.Value);
                    g.FillPolygon(corner, [new Point(rect.Width - 12, 0), new Point(rect.Width, 0), new Point(rect.Width, 12)]);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    toolTip.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>The gem list that opens over the right side of the page.</summary>
        private sealed class DrawerPanel : UnstyledPanel
        {
            public DrawerPanel()
            {
                BackColor = Palette.Drawer;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var rect = ClientRectangle;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return;
                }

                using var brush = new LinearGradientBrush(rect, Palette.DrawerLight, Palette.Drawer, 90f);
                e.Graphics.FillRectangle(brush, rect);
                using var edge = new Pen(Palette.Border);
                e.Graphics.DrawRectangle(edge, 0, 0, rect.Width - 1, rect.Height - 1);
            }
        }

        /// <summary>A section caption in capitals.</summary>
        private sealed class CaptionLabel : Control
        {
            public CaptionLabel(string text)
            {
                Text = text;
                Font = UiFont(10f, FontStyle.Bold);
                BackColor = Palette.Drawer;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var background = new SolidBrush(Palette.Drawer))
                {
                    e.Graphics.FillRectangle(background, ClientRectangle);
                }

                System.Windows.Forms.TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private sealed class Spacer : Control
        {
            public Spacer(int width)
            {
                Size = new Size(width, 1);
                Margin = Padding.Empty;
            }
        }

        /// <summary>
        /// A flat uppercase button: dark with a hairline border, green when primary or toggled on, or a
        /// raised grey one for the main action.
        /// </summary>
        private sealed class ShowcaseButton : Control
        {
            private bool hover;
            private bool pressed;
            private bool toggled;

            public bool Primary { get; init; }
            public bool Raised { get; init; }
            public bool Small { get; init; }

            public bool Toggled
            {
                get => toggled;
                set
                {
                    toggled = value;
                    Invalidate();
                }
            }

            public ShowcaseButton(string text, Action onClick)
            {
                Text = text.ToUpperInvariant();
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                Click += (_, _) => onClick();
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                Font = UiFont(Raised ? 12f : Small ? 8.5f : 9.5f, FontStyle.Bold);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var rect = ClientRectangle;
                var accent = Primary || toggled;

                if (Raised)
                {
                    var top = pressed ? Color.FromArgb(78, 86, 100) : hover ? Color.FromArgb(112, 122, 138) : Color.FromArgb(96, 105, 120);
                    var bottom = pressed ? Color.FromArgb(58, 64, 76) : hover ? Color.FromArgb(82, 90, 104) : Color.FromArgb(68, 75, 88);
                    using var raised = new LinearGradientBrush(rect, top, bottom, 90f);
                    g.FillRectangle(raised, rect);
                    using var light = new Pen(Color.FromArgb(150, 160, 175));
                    g.DrawLine(light, 0, 0, rect.Width, 0);
                }
                else
                {
                    var fill = accent
                        ? (pressed ? Color.FromArgb(52, 92, 44) : hover ? Color.FromArgb(70, 120, 56) : Color.FromArgb(58, 104, 48))
                        : pressed ? Palette.Border : hover ? Palette.TileHover : Palette.Tile;
                    using var brush = new SolidBrush(fill);
                    g.FillRectangle(brush, rect);
                    using var border = new Pen(accent ? Color.FromArgb(120, 170, 100) : Palette.Border);
                    g.DrawRectangle(border, 0, 0, rect.Width - 1, rect.Height - 1);
                }

                var textColor = Primary ? Color.FromArgb(236, 248, 226) : toggled ? Palette.Arcana : Palette.Text;
                System.Windows.Forms.TextRenderer.DrawText(g, Text, Font, rect, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>A framed text box.</summary>
        private sealed class FieldBox : UnstyledPanel
        {
            public FieldBox(TextBox textBox)
            {
                BackColor = Palette.Tile;
                Padding = new Padding(10, 9, 10, 6);
                textBox.BorderStyle = BorderStyle.None;
                textBox.Dock = DockStyle.Fill;
                textBox.Font = UiFont(10.5f);
                textBox.BackColor = Palette.Tile;
                textBox.ForeColor = Palette.Text;
                Controls.Add(textBox);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using var border = new Pen(Palette.Border);
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        }

        /// <summary>A tile of one gem: its icon over its name, outlined in green when equipped.</summary>
        private sealed class GemTile : Control
        {
            private readonly PrismaticGemIcons icons;
            private bool hover;
            private bool selected;

            public PrismaticGem Gem { get; }

            public bool Selected
            {
                get => selected;
                set
                {
                    if (selected != value)
                    {
                        selected = value;
                        Invalidate();
                    }
                }
            }

            public GemTile(PrismaticGem gem, PrismaticGemIcons icons)
            {
                Gem = gem;
                this.icons = icons;
                Cursor = Cursors.Hand;
                Font = UiFont(9f, FontStyle.Bold);
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var rect = ClientRectangle;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                using (var background = new SolidBrush(hover || selected ? Palette.TileHover : Palette.Tile))
                {
                    g.FillRectangle(background, rect);
                }

                // A soft pool of the gem color behind the icon
                var iconSize = (int)(rect.Width * 0.58f);
                var iconRect = new Rectangle((rect.Width - iconSize) / 2, (int)(rect.Height * 0.08f), iconSize, iconSize);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(Rectangle.Inflate(iconRect, iconSize / 6, iconSize / 6));
                    using var glow = new PathGradientBrush(path)
                    {
                        CenterColor = Color.FromArgb(hover || selected ? 90 : 55, Gem.Color),
                        SurroundColors = [Color.FromArgb(0, Gem.Color)],
                    };
                    g.FillPath(glow, path);
                }

                g.DrawImage(icons.Get(Gem, iconSize), iconRect);

                var textRect = new Rectangle(4, iconRect.Bottom + 2, rect.Width - 8, rect.Height - iconRect.Bottom - 4);
                System.Windows.Forms.TextRenderer.DrawText(g, Gem.Name, Font, textRect, selected ? Palette.Arcana : Palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                using var border = new Pen(selected ? Palette.Arcana : hover ? Palette.Border : Color.FromArgb(40, 37, 54), selected ? 2f : 1f);
                g.SmoothingMode = SmoothingMode.None;
                g.DrawRectangle(border, selected ? 1 : 0, selected ? 1 : 0, rect.Width - (selected ? 2 : 1), rect.Height - (selected ? 2 : 1));
            }
        }

        /// <summary>The equipped gem: a large icon with its name, hex and local name.</summary>
        private sealed class GemCard : Control
        {
            private readonly PrismaticGemIcons icons;
            private PrismaticGem? gem;
            private readonly Font titleFont = UiFont(15f, FontStyle.Bold);
            private readonly Font subFont = UiFont(9.5f);
            private readonly Font tagFont = UiFont(8.5f, FontStyle.Bold);

            public PrismaticGem? Gem
            {
                get => gem;
                set
                {
                    gem = value;
                    Invalidate();
                }
            }

            public GemCard(PrismaticGemIcons icons)
            {
                this.icons = icons;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var rect = ClientRectangle;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var tint = gem?.Color ?? Palette.Dim;
                using (var baseBrush = new SolidBrush(Palette.Tile))
                {
                    g.FillRectangle(baseBrush, rect);
                }
                using (var background = new LinearGradientBrush(rect, Color.FromArgb(70, tint), Palette.Tile, 0f))
                {
                    g.FillRectangle(background, rect);
                }

                var iconSize = rect.Height - 16;
                var iconRect = new Rectangle(10, 8, iconSize, iconSize);
                g.DrawImage(icons.Get(gem, iconSize), iconRect);

                var x = iconRect.Right + 12;
                var textWidth = rect.Width - x - 8;
                System.Windows.Forms.TextRenderer.DrawText(g, gem == null ? "ОРИГИНАЛЬНЫЙ ЦВЕТ" : "ПРИЗМАТИЧЕСКИЙ", tagFont,
                    new Rectangle(x, rect.Height * 12 / 100, textWidth, rect.Height * 16 / 100), gem == null ? Palette.Dim : Palette.Arcana, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                System.Windows.Forms.TextRenderer.DrawText(g, gem?.Name ?? "Без самоцвета", titleFont,
                    new Rectangle(x, rect.Height * 28 / 100, textWidth, rect.Height * 30 / 100), Palette.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                var sub = gem == null ? "Аркана в стандартных цветах" : gem.LocalName.Length > 0 ? $"#{gem.Hex}  ·  {gem.LocalName}" : $"#{gem.Hex}";
                System.Windows.Forms.TextRenderer.DrawText(g, sub, subFont,
                    new Rectangle(x, rect.Height * 60 / 100, textWidth, rect.Height * 20 / 100), Palette.Dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                using var border = new Pen(gem == null ? Palette.Border : Color.FromArgb(150, tint));
                g.SmoothingMode = SmoothingMode.None;
                g.DrawRectangle(border, 0, 0, rect.Width - 1, rect.Height - 1);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    titleFont.Dispose();
                    subFont.Dispose();
                    tagFont.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
