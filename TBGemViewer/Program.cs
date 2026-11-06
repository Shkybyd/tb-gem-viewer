using System.Numerics;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Input;
using ValveResourceFormat.Renderer.SceneEnvironment;

namespace TBGemViewer;

/// <summary>
/// The showcase window: the scene renderer of Source 2 Viewer in a GLFW window, which runs on Windows and Linux,
/// with the page drawn over it by ImGui.
/// </summary>
sealed class ShowcaseWindow : GameWindow
{
    // A narrow lens like the loadout camera, which keeps the hero from bending at the edges
    private const float FieldOfView = 40;

    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly GameFileLoader files;
    private readonly RendererContext rendererContext;

    private Renderer? renderer;
    private Framebuffer? framebuffer;
    private TextRenderer? textRenderer;
    private UserInput? input;
    private ImGuiRenderer? imgui;
    private GemScene? gemScene;
    private ShowcaseUi? ui;

    private Vector3 cameraTarget;
    private Vector3 cameraDirection;
    private float cameraDistance;
    private float minCameraDistance;
    private float maxCameraDistance;
    private bool turning;
    private float lastMouseX;

    public ShowcaseWindow(GameWindowSettings gameSettings, NativeWindowSettings nativeSettings, string dotaPackage)
        : base(gameSettings, nativeSettings)
    {
        loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        logger = loggerFactory.CreateLogger("TBGemViewer");

#pragma warning disable CA2000 // The loader owns the package and the renderer context owns the loader
        var package = Package.Open(dotaPackage);
        files = new GameFileLoader(package, dotaPackage, logger);
#pragma warning restore CA2000
        rendererContext = new RendererContext(files, logger)
        {
            FieldOfView = FieldOfView,
        };
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        rendererContext.Device.CreateContext().Begin();
        GLEnvironment.Initialize(logger);
        GLEnvironment.SetDefaultRenderState();

        renderer = new Renderer(rendererContext);
        input = new UserInput(renderer) { SmoothCameraEnabled = false };

        textRenderer = new TextRenderer(rendererContext, renderer.Camera);
        textRenderer.Load();

        framebuffer = Framebuffer.Prepare("MainFramebuffer", 4, 4, 4, ImageFormat.RGBA16161616F, ImageFormat.D32);
        framebuffer.Initialize();

        renderer.Postprocess.Load(framebuffer.NumSamples);
        renderer.Initialize();
        renderer.MainFramebuffer = framebuffer;
        renderer.LoadRendererResources();

        using (var cubemap = typeof(ShowcaseWindow).Assembly.GetManifestResourceStream("DefaultCubemap"))
        using (var resource = new Resource { FileName = "vrf_default_cubemap.vtex_c" })
        {
            resource.Read(cubemap!);
            Renderer.LoadDefaultLighting(renderer.Scene, resource);
        }

        gemScene = new GemScene(renderer.Scene, logger);

        renderer.Scene.PostProcessInfo.AddPostProcessVolume(new ScenePostProcessVolume(renderer.Scene)
        {
            HasBloom = true,
            IsMaster = true,
        });

        foreach (var scene in renderer.Scenes)
        {
            scene.Initialize();
        }

        renderer.BaseBackground?.SetLobbyBackground(true);
        rendererContext.ShaderLoader.LinkLoadedShaders();

        var scale = 1f;
        if (TryGetCurrentMonitorScale(out var scaleX, out _))
        {
            scale = Math.Clamp(scaleX, 1f, 3f);
        }

        imgui = new ImGuiRenderer();
        ui = new ShowcaseUi(files, gemScene, LobbyCamera, GameCamera, scale);
        ui.AddFonts(files);
        imgui.BuildFonts();
        ui.ApplyStyle();

        ResizeScene(FramebufferSize.X, FramebufferSize.Y);
        LobbyCamera();
    }

    // The hero page: a front view of the loadout pose
    private void LobbyCamera()
    {
        SetCamera(GemScene.HeroFocus - new Vector3(0, 0, 6), new Vector3(640, 0, 105), 330, 1000);
        gemScene!.PlayActivity("ACT_DOTA_LOADOUT", loop: true);
    }

    // The Dota game camera looks north and about 60 degrees down from 1134 units away
    private void GameCamera()
    {
        var pitch = float.DegreesToRadians(60);
        SetCamera(new Vector3(0, 0, 60), new Vector3(0, -MathF.Cos(pitch), MathF.Sin(pitch)) * 1134, 600, 1600);
        gemScene!.PlayActivity("ACT_DOTA_IDLE", loop: true);
    }

    private void SetCamera(Vector3 target, Vector3 offset, float minDistance, float maxDistance)
    {
        cameraTarget = target;
        cameraDirection = Vector3.Normalize(offset);
        cameraDistance = offset.Length();
        minCameraDistance = minDistance;
        maxCameraDistance = maxDistance;
        ApplyCamera();
    }

    private void ApplyCamera()
    {
        input!.Camera.SetLocation(cameraTarget + cameraDirection * cameraDistance);
        input.Camera.LookAt(cameraTarget);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        if (renderer == null || input == null || gemScene == null || ui == null)
        {
            return;
        }

        var deltaTime = (float)args.Time;

        // As on the hero page, a sideways drag over the scene turns the hero and the camera stays put
        var mouseX = MouseState.X;
        if (MouseState.IsButtonPressed(MouseButton.Left) && !ShowcaseUi.WantsMouse)
        {
            turning = true;
        }
        else if (!MouseState.IsButtonDown(MouseButton.Left))
        {
            turning = false;
        }

        if (turning)
        {
            gemScene.Turn((mouseX - lastMouseX) * 0.006f);
        }

        lastMouseX = mouseX;

        gemScene.Update(deltaTime);
        input.Tick(deltaTime, TrackedKeys.None, Vector2.Zero, renderer.Camera);

        renderer.Update(new Scene.UpdateContext
        {
            Camera = renderer.Camera,
            TextRenderer = textRenderer!,
            Timestep = deltaTime,
        });

        input.LateUpdate(renderer.Camera);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        ImGuiRenderer.AddWheel(e.OffsetY);

        if (ui != null && !ShowcaseUi.WantsMouse && input != null)
        {
            cameraDistance = Math.Clamp(cameraDistance * MathF.Pow(0.9f, e.OffsetY), minCameraDistance, maxCameraDistance);
            ApplyCamera();
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        ImGuiRenderer.AddCharacter((uint)e.Unicode);
    }

    // The scene renders at the size of the framebuffer, which on a scaled desktop is not the size of the window
    protected override void OnFramebufferResize(FramebufferResizeEventArgs e)
    {
        base.OnFramebufferResize(e);
        ResizeScene(e.Width, e.Height);
    }

    private void ResizeScene(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        GL.Viewport(0, 0, width, height);
        renderer?.Camera.SetViewportSize(width, height);
        input?.Camera.SetViewportSize(width, height);
        framebuffer?.Resize(width, height);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        if (renderer == null || framebuffer == null || imgui == null || ui == null)
        {
            return;
        }

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        renderer.Render(framebuffer);
        renderer.PostprocessRender(framebuffer, Framebuffer.GLDefaultFramebuffer, flipY: false);
        textRenderer!.Render(renderer.Camera);

        ImGuiRenderer.NewFrame(this, (float)args.Time);
        ui.Draw(new Vector2(ClientSize.X, ClientSize.Y));
        imgui.Render(FramebufferSize.X, FramebufferSize.Y);

        SwapBuffers();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            imgui?.Dispose();
            renderer?.Dispose();
            rendererContext.Dispose();
            files.Dispose();
            loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }
}

static class Program
{
    private const int DotaAppId = 570;

    private static int Main(string[] args)
    {
        var dotaPackage = args.FirstOrDefault(static a => a.EndsWith("pak01_dir.vpk", StringComparison.OrdinalIgnoreCase))
            ?? FindDota();

        if (dotaPackage == null || !File.Exists(dotaPackage))
        {
            Console.Error.WriteLine("Dota 2 was not found in the Steam libraries.");
            Console.Error.WriteLine("Pass its package as an argument, for example:");
            Console.Error.WriteLine("  TBGemViewer \"~/.local/share/Steam/steamapps/common/dota 2 beta/game/dota/pak01_dir.vpk\"");
            return 1;
        }

        var nativeSettings = new NativeWindowSettings
        {
            APIVersion = GLEnvironment.RequiredVersion,
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new(1600, 900),
            WindowState = WindowState.Maximized,
            Title = "TB Gem Viewer - Fractal Horns of Inner Abysm",
            Vsync = VSyncMode.On,
        };

        using var window = new ShowcaseWindow(new GameWindowSettings { UpdateFrequency = 0 }, nativeSettings, dotaPackage);
        window.Run();
        return 0;
    }

    private static string? FindDota()
    {
        var game = GameFolderLocator.FindSteamGameByAppId(DotaAppId);
        return game is { } found ? Path.Join(found.GamePath, "game", "dota", "pak01_dir.vpk") : null;
    }
}
