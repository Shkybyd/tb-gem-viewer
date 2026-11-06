using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace TBGemViewer;

/// <summary>
/// Feeds GLFW input to ImGui and draws its lists with OpenGL over the frame. It puts back every piece of GL
/// state it touches, because the scene renderer keeps its own record of that state.
/// </summary>
sealed class ImGuiRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        uniform mat4 projection;
        layout (location = 0) in vec2 inPosition;
        layout (location = 1) in vec2 inTexCoord;
        layout (location = 2) in vec4 inColor;
        out vec4 color;
        out vec2 texCoord;
        void main()
        {
            gl_Position = projection * vec4(inPosition, 0, 1);
            color = inColor;
            texCoord = inTexCoord;
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        uniform sampler2D fontTexture;
        in vec4 color;
        in vec2 texCoord;
        out vec4 outputColor;
        void main()
        {
            outputColor = color * texture(fontTexture, texCoord);
        }
        """;

    private readonly int program;
    private readonly int projectionLocation;
    private readonly int vertexArray;
    private readonly int vertexBuffer;
    private readonly int indexBuffer;
    private int fontTexture;
    private int vertexBufferSize = 10000;
    private int indexBufferSize = 2000;

    public ImGuiRenderer()
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        unsafe
        {
            // No imgui.ini next to the program
            io.NativePtr->IniFilename = null;
        }

        program = CompileProgram();
        projectionLocation = GL.GetUniformLocation(program, "projection");

        vertexArray = GL.GenVertexArray();
        vertexBuffer = GL.GenBuffer();
        indexBuffer = GL.GenBuffer();

        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVertexArray);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int previousArrayBuffer);

        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = Unsafe.SizeOf<ImDrawVert>();
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(previousVertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousArrayBuffer);
    }

    private static int CompileProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
            {
                throw new InvalidOperationException($"ImGui {type} failed: {GL.GetShaderInfoLog(shader)}");
            }

            return shader;
        }

        var vertex = Compile(ShaderType.VertexShader, VertexSource);
        var fragment = Compile(ShaderType.FragmentShader, FragmentSource);
        var result = GL.CreateProgram();
        GL.AttachShader(result, vertex);
        GL.AttachShader(result, fragment);
        GL.LinkProgram(result);
        GL.DetachShader(result, vertex);
        GL.DetachShader(result, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return result;
    }

    /// <summary>Uploads the font atlas once the fonts have been added to it.</summary>
    public void BuildFonts()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);

        if (fontTexture != 0)
        {
            GL.DeleteTexture(fontTexture);
        }

        fontTexture = CreateTexture(pixels, width, height);
        io.Fonts.SetTexID(fontTexture);
        io.Fonts.ClearTexData();
    }

    /// <summary>Creates an RGBA texture for ImGui images, keeping the bound texture as it was.</summary>
    public static int CreateTexture(IntPtr rgbaPixels, int width, int height)
    {
        GL.GetInteger(GetPName.TextureBinding2D, out int previous);
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgbaPixels);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.BindTexture(TextureTarget.Texture2D, previous);
        return texture;
    }

    public static void NewFrame(GameWindow window, float deltaTime)
    {
        var io = ImGui.GetIO();
        // Scaled desktops, as on Wayland, give the window fewer units than the framebuffer has pixels
        io.DisplaySize = new System.Numerics.Vector2(window.ClientSize.X, window.ClientSize.Y);
        io.DisplayFramebufferScale = window.ClientSize.X > 0 && window.ClientSize.Y > 0
            ? new System.Numerics.Vector2((float)window.FramebufferSize.X / window.ClientSize.X, (float)window.FramebufferSize.Y / window.ClientSize.Y)
            : System.Numerics.Vector2.One;
        io.DeltaTime = deltaTime > 0 ? deltaTime : 1f / 60f;

        var mouse = window.MouseState;
        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse.IsButtonDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, mouse.IsButtonDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, mouse.IsButtonDown(MouseButton.Middle));

        var keyboard = window.KeyboardState;
        foreach (var (key, imguiKey) in KeyMap)
        {
            io.AddKeyEvent(imguiKey, keyboard.IsKeyDown(key));
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));

        ImGui.NewFrame();
    }

    private static readonly (Keys Key, ImGuiKey ImGuiKey)[] KeyMap =
    [
        (Keys.Tab, ImGuiKey.Tab), (Keys.Left, ImGuiKey.LeftArrow), (Keys.Right, ImGuiKey.RightArrow),
        (Keys.Up, ImGuiKey.UpArrow), (Keys.Down, ImGuiKey.DownArrow), (Keys.Home, ImGuiKey.Home),
        (Keys.End, ImGuiKey.End), (Keys.Delete, ImGuiKey.Delete), (Keys.Backspace, ImGuiKey.Backspace),
        (Keys.Enter, ImGuiKey.Enter), (Keys.KeyPadEnter, ImGuiKey.KeypadEnter), (Keys.Escape, ImGuiKey.Escape),
        (Keys.A, ImGuiKey.A), (Keys.C, ImGuiKey.C), (Keys.V, ImGuiKey.V), (Keys.X, ImGuiKey.X),
        (Keys.LeftControl, ImGuiKey.LeftCtrl), (Keys.RightControl, ImGuiKey.RightCtrl),
        (Keys.LeftShift, ImGuiKey.LeftShift), (Keys.RightShift, ImGuiKey.RightShift),
    ];

    public static void AddWheel(float delta) => ImGui.GetIO().AddMouseWheelEvent(0, delta);

    public static void AddCharacter(uint character) => ImGui.GetIO().AddInputCharacter(character);

    /// <summary>Draws the ImGui frame into the bound framebuffer, then puts the GL state back.</summary>
    public void Render(int width, int height)
    {
        ImGui.Render();
        var drawData = ImGui.GetDrawData();
        if (drawData.CmdListsCount == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        // Back up everything that is changed below
        GL.GetInteger(GetPName.CurrentProgram, out int lastProgram);
        GL.GetInteger(GetPName.ActiveTexture, out int lastActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int lastTexture);
        GL.GetInteger(GetPName.SamplerBinding, out int lastSampler);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int lastArrayBuffer);
        GL.GetInteger(GetPName.VertexArrayBinding, out int lastVertexArray);
        var lastViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, lastViewport);
        var lastScissor = new int[4];
        GL.GetInteger(GetPName.ScissorBox, lastScissor);
        GL.GetInteger(GetPName.BlendSrcRgb, out int lastBlendSrcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int lastBlendDstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int lastBlendSrcAlpha);
        GL.GetInteger(GetPName.BlendDstAlpha, out int lastBlendDstAlpha);
        GL.GetInteger(GetPName.BlendEquationRgb, out int lastBlendEquationRgb);
        GL.GetInteger(GetPName.BlendEquationAlpha, out int lastBlendEquationAlpha);
        var colorMask = new bool[4];
        GL.GetBoolean(GetPName.ColorWritemask, colorMask);
        GL.GetBoolean(GetPName.DepthWritemask, out var lastDepthMask);
        var lastBlend = GL.IsEnabled(EnableCap.Blend);
        var lastCull = GL.IsEnabled(EnableCap.CullFace);
        var lastDepth = GL.IsEnabled(EnableCap.DepthTest);
        var lastStencil = GL.IsEnabled(EnableCap.StencilTest);
        var lastScissorTest = GL.IsEnabled(EnableCap.ScissorTest);
        var lastSrgb = GL.IsEnabled(EnableCap.FramebufferSrgb);

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.StencilTest);
        GL.Disable(EnableCap.FramebufferSrgb);
        GL.Enable(EnableCap.ScissorTest);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(false);
        GL.Viewport(0, 0, width, height);

        var displayPos = drawData.DisplayPos;
        var displaySize = drawData.DisplaySize;
        var framebufferScale = drawData.FramebufferScale;
        var projection = OpenTK.Mathematics.Matrix4.CreateOrthographicOffCenter(
            displayPos.X, displayPos.X + displaySize.X, displayPos.Y + displaySize.Y, displayPos.Y, -1f, 1f);

        GL.UseProgram(program);
        GL.UniformMatrix4(projectionLocation, false, ref projection);
        GL.Uniform1(GL.GetUniformLocation(program, "fontTexture"), 0);
        GL.BindSampler(0, 0);
        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);

        var vertexSize = Unsafe.SizeOf<ImDrawVert>();

        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var list = drawData.CmdLists[n];

            var vertexBytes = list.VtxBuffer.Size * vertexSize;
            if (vertexBytes > vertexBufferSize)
            {
                vertexBufferSize = Math.Max(vertexBufferSize * 2, vertexBytes);
                GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            var indexBytes = list.IdxBuffer.Size * sizeof(ushort);
            if (indexBytes > indexBufferSize)
            {
                indexBufferSize = Math.Max(indexBufferSize * 2, indexBytes);
                GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexBytes, list.VtxBuffer.Data);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexBytes, list.IdxBuffer.Data);

            for (var i = 0; i < list.CmdBuffer.Size; i++)
            {
                var command = list.CmdBuffer[i];
                if (command.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                var clip = command.ClipRect;
                var clipX = (int)((clip.X - displayPos.X) * framebufferScale.X);
                var clipY = (int)((clip.Y - displayPos.Y) * framebufferScale.Y);
                var clipW = (int)((clip.Z - clip.X) * framebufferScale.X);
                var clipH = (int)((clip.W - clip.Y) * framebufferScale.Y);
                if (clipW <= 0 || clipH <= 0)
                {
                    continue;
                }

                GL.Scissor(clipX, height - clipY - clipH, clipW, clipH);
                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)command.ElemCount, DrawElementsType.UnsignedShort,
                    (IntPtr)(command.IdxOffset * sizeof(ushort)), (int)command.VtxOffset);
            }
        }

        // Put the state back as the scene renderer left it
        GL.UseProgram(lastProgram);
        GL.BindTexture(TextureTarget.Texture2D, lastTexture);
        GL.BindSampler(0, lastSampler);
        GL.ActiveTexture((TextureUnit)lastActiveTexture);
        GL.BindVertexArray(lastVertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, lastArrayBuffer);
        GL.BlendEquationSeparate((BlendEquationMode)lastBlendEquationRgb, (BlendEquationMode)lastBlendEquationAlpha);
        GL.BlendFuncSeparate((BlendingFactorSrc)lastBlendSrcRgb, (BlendingFactorDest)lastBlendDstRgb, (BlendingFactorSrc)lastBlendSrcAlpha, (BlendingFactorDest)lastBlendDstAlpha);
        SetEnabled(EnableCap.Blend, lastBlend);
        SetEnabled(EnableCap.CullFace, lastCull);
        SetEnabled(EnableCap.DepthTest, lastDepth);
        SetEnabled(EnableCap.StencilTest, lastStencil);
        SetEnabled(EnableCap.ScissorTest, lastScissorTest);
        SetEnabled(EnableCap.FramebufferSrgb, lastSrgb);
        GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
        GL.DepthMask(lastDepthMask);
        GL.Viewport(lastViewport[0], lastViewport[1], lastViewport[2], lastViewport[3]);
        GL.Scissor(lastScissor[0], lastScissor[1], lastScissor[2], lastScissor[3]);
    }

    private static void SetEnabled(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            GL.Enable(cap);
        }
        else
        {
            GL.Disable(cap);
        }
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(vertexArray);
        GL.DeleteBuffer(vertexBuffer);
        GL.DeleteBuffer(indexBuffer);
        GL.DeleteProgram(program);

        if (fontTexture != 0)
        {
            GL.DeleteTexture(fontTexture);
        }

        ImGui.DestroyContext();
    }

    /// <summary>Adds a TrueType font from memory. ImGui keeps and later frees its own copy of the bytes.</summary>
    public static ImFontPtr AddFont(byte[] data, float sizePixels, IntPtr glyphRanges)
    {
        var memory = ImGui.MemAlloc((uint)data.Length);
        Marshal.Copy(data, 0, memory, data.Length);
        return ImGui.GetIO().Fonts.AddFontFromMemoryTTF(memory, data.Length, sizePixels, null, glyphRanges);
    }
}
