using Android.Graphics;
using Android.Opengl;
using Java.Nio;
using AndroidSurface = Android.Views.Surface;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>EGL context, shaders and drawing primitives. Every member must be used on the compositor thread.</summary>
internal sealed class GlRenderer : IDisposable
{
    private const string VertexShader = """
        attribute vec2 aPosition;
        uniform vec4 uRect;
        uniform mat4 uTexMatrix;
        varying vec2 vTex;
        void main() {
            gl_Position = vec4(mix(uRect.x, uRect.z, aPosition.x), mix(uRect.y, uRect.w, aPosition.y), 0.0, 1.0);
            vTex = (uTexMatrix * vec4(aPosition, 0.0, 1.0)).xy;
        }
        """;
    // Raw string literals drop their final newline; the preprocessor needs one after #endif.
    private const string Precision = """
        #ifdef GL_FRAGMENT_PRECISION_HIGH
        precision highp float;
        #else
        precision mediump float;
        #endif

        """;
    private const string TextureShader = Precision + """
        varying vec2 vTex;
        uniform sampler2D uTexture;
        uniform float uAlpha;
        void main() { gl_FragColor = texture2D(uTexture, vTex) * uAlpha; }
        """;
    private const string ExternalShader = "#extension GL_OES_EGL_image_external : require\n" + Precision + """
        varying vec2 vTex;
        uniform samplerExternalOES uTexture;
        uniform float uAlpha;
        void main() { gl_FragColor = vec4(texture2D(uTexture, vTex).rgb, 1.0) * uAlpha; }
        """;
    private const string ColorShader = Precision + """
        varying vec2 vTex;
        uniform vec4 uColor;
        void main() { gl_FragColor = uColor; }
        """;

    private EGLDisplay? display;
    private EGLContext? context;
    private EGLConfig? config;
    private EGLSurface? pbuffer;
    private Program? texture, external, color;
    private int quad;

    public void Initialize()
    {
        display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (display == EGL14.EglNoDisplay) throw new InvalidOperationException("No EGL display.");
        var version = new int[2];
        if (!EGL14.EglInitialize(display, version, 0, version, 1)) throw new InvalidOperationException("EGL could not be initialized.");
        int[] attributes = [EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
            EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit, EGLExt.EglRecordableAndroid, 1, EGL14.EglNone];
        var configs = new EGLConfig[1]; var count = new int[1];
        if (!EGL14.EglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) || count[0] == 0)
            throw new InvalidOperationException("No recordable RGBA8888 EGL configuration.");
        config = configs[0];
        context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, [EGL14.EglContextClientVersion, 2, EGL14.EglNone], 0);
        if (context == EGL14.EglNoContext) throw new InvalidOperationException("Could not create an OpenGL ES 2 context.");
        pbuffer = EGL14.EglCreatePbufferSurface(display, config, [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone], 0)
            ?? throw new InvalidOperationException("Could not create an offscreen EGL surface.");
        MakeCurrent(pbuffer);
        texture = new Program(TextureShader);
        external = new Program(ExternalShader);
        color = new Program(ColorShader);
        var buffers = new int[1];
        GLES20.GlGenBuffers(1, buffers, 0);
        quad = buffers[0];
        GLES20.GlBindBuffer(GLES20.GlArrayBuffer, quad);
        var vertices = ByteBuffer.AllocateDirect(8 * 4).Order(ByteOrder.NativeOrder()!)!.AsFloatBuffer()!;
        vertices.Put([0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f])!.Position(0);
        GLES20.GlBufferData(GLES20.GlArrayBuffer, 8 * 4, vertices, GLES20.GlStaticDraw);
        GLES20.GlDisable(GLES20.GlDepthTest);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
        var maximum = new int[1];
        GLES20.GlGetIntegerv(GLES20.GlMaxTextureSize, maximum, 0);
        MaxTextureSize = maximum[0];
    }

    public int MaxTextureSize { get; private set; }

    public EGLSurface CreateWindowSurface(AndroidSurface surface)
    {
        var created = EGL14.EglCreateWindowSurface(display, config, surface, [EGL14.EglNone], 0);
        if (created is null || created == EGL14.EglNoSurface) throw new InvalidOperationException($"Could not create a window surface (EGL error 0x{EGL14.EglGetError():X}).");
        return created;
    }

    public void DestroySurface(EGLSurface surface)
    {
        if (surface == EGL14.EglGetCurrentSurface(EGL14.EglDraw)) MakeCurrent(pbuffer!);
        EGL14.EglDestroySurface(display, surface);
    }

    public void MakeCurrent(EGLSurface surface)
    {
        if (!EGL14.EglMakeCurrent(display, surface, surface, context))
            throw new InvalidOperationException($"eglMakeCurrent failed (0x{EGL14.EglGetError():X}).");
    }

    public void MakeCurrentOffscreen() => MakeCurrent(pbuffer!);
    public (int Width, int Height) SurfaceSize(EGLSurface surface)
    {
        var width = new int[1]; var height = new int[1];
        if (!EGL14.EglQuerySurface(display, surface, EGL14.EglWidth, width, 0) ||
            !EGL14.EglQuerySurface(display, surface, EGL14.EglHeight, height, 0))
            throw new InvalidOperationException($"Could not query the preview buffer size (EGL error 0x{EGL14.EglGetError():X}).");
        return (width[0], height[0]);
    }

    public void SetSwapInterval(int interval) => EGL14.EglSwapInterval(display, interval);
    public void SetPresentationTime(EGLSurface surface, long nanoseconds) => EGLExt.EglPresentationTimeANDROID(display, surface, nanoseconds);
    public bool Swap(EGLSurface surface) => EGL14.EglSwapBuffers(display, surface);

    public static int CreateTexture(bool external)
    {
        var target = external ? GLES11Ext.GlTextureExternalOes : GLES20.GlTexture2d;
        var ids = new int[1];
        GLES20.GlGenTextures(1, ids, 0);
        GLES20.GlBindTexture(target, ids[0]);
        GLES20.GlTexParameteri(target, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(target, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(target, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(target, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        return ids[0];
    }

    public static void DeleteTexture(int id) { if (id != 0) GLES20.GlDeleteTextures(1, [id], 0); }

    public static void Upload(int textureId, Bitmap bitmap)
    {
        GLES20.GlBindTexture(GLES20.GlTexture2d, textureId);
        GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bitmap, 0);
    }

    public sealed class Framebuffer(int id, int texture, int width, int height) : IDisposable
    {
        public int Id { get; } = id; public int Texture { get; } = texture;
        public int Width { get; } = width; public int Height { get; } = height;
        public void Dispose() { GLES20.GlDeleteFramebuffers(1, [Id], 0); DeleteTexture(Texture); }
    }

    public static Framebuffer CreateFramebuffer(int width, int height)
    {
        var texture = CreateTexture(false);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, null);
        var ids = new int[1];
        GLES20.GlGenFramebuffers(1, ids, 0);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, ids[0]);
        GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0, GLES20.GlTexture2d, texture, 0);
        var status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        if (status != GLES20.GlFramebufferComplete)
        {
            GLES20.GlDeleteFramebuffers(1, ids, 0); DeleteTexture(texture);
            throw new InvalidOperationException($"The GPU cannot render a {width}×{height} frame (framebuffer status 0x{status:X}).");
        }
        return new(ids[0], texture, width, height);
    }

    public static void BindTarget(Framebuffer? framebuffer, int width, int height)
    {
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer?.Id ?? 0);
        GLES20.GlViewport(0, 0, width, height);
    }

    public static void Clear(float red, float green, float blue)
    {
        GLES20.GlClearColor(red, green, blue, 1);
        GLES20.GlClear(GLES20.GlColorBufferBit);
    }

    /// <summary>Draws a texture into a canvas-normalized rectangle (x, y, width, height; y down).</summary>
    public void DrawTexture(int textureId, bool isExternal, (double X, double Y, double W, double H) rect, float[] texMatrix, float alpha)
    {
        var program = isExternal ? external! : texture!;
        program.Use(quad, rect, texMatrix);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(isExternal ? GLES11Ext.GlTextureExternalOes : GLES20.GlTexture2d, textureId);
        GLES20.GlUniform1i(program.Texture, 0);
        GLES20.GlUniform1f(program.Alpha, alpha);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
    }

    public void DrawColor((double X, double Y, double W, double H) rect, float red, float green, float blue, float alpha)
    {
        color!.Use(quad, rect, IdentityMatrix);
        GLES20.GlUniform4f(color.Color, red * alpha, green * alpha, blue * alpha, alpha);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
    }

    public static readonly float[] IdentityMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public void Dispose()
    {
        if (display is null) return;
        EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
        if (pbuffer is not null) EGL14.EglDestroySurface(display, pbuffer);
        if (context is not null) EGL14.EglDestroyContext(display, context);
        EGL14.EglReleaseThread();
        display = null;
    }

    private sealed class Program
    {
        private readonly int id, position, rect, matrix;
        public int Texture { get; }
        public int Alpha { get; }
        public int Color { get; }

        public Program(string fragment)
        {
            id = GLES20.GlCreateProgram();
            GLES20.GlAttachShader(id, Compile(GLES20.GlVertexShader, VertexShader));
            GLES20.GlAttachShader(id, Compile(GLES20.GlFragmentShader, fragment));
            GLES20.GlLinkProgram(id);
            var status = new int[1];
            GLES20.GlGetProgramiv(id, GLES20.GlLinkStatus, status, 0);
            if (status[0] == 0) throw new InvalidOperationException("Shader link failed: " + GLES20.GlGetProgramInfoLog(id));
            position = GLES20.GlGetAttribLocation(id, "aPosition");
            rect = GLES20.GlGetUniformLocation(id, "uRect");
            matrix = GLES20.GlGetUniformLocation(id, "uTexMatrix");
            Texture = GLES20.GlGetUniformLocation(id, "uTexture");
            Alpha = GLES20.GlGetUniformLocation(id, "uAlpha");
            Color = GLES20.GlGetUniformLocation(id, "uColor");
        }

        public void Use(int quad, (double X, double Y, double W, double H) r, float[] texMatrix)
        {
            GLES20.GlUseProgram(id);
            GLES20.GlBindBuffer(GLES20.GlArrayBuffer, quad);
            GLES20.GlEnableVertexAttribArray(position);
            GLES20.GlVertexAttribPointer(position, 2, GLES20.GlFloat, false, 8, 0);
            // Normalized (y down) to clip space (y up).
            GLES20.GlUniform4f(rect, (float)(r.X * 2 - 1), (float)(1 - r.Y * 2), (float)((r.X + r.W) * 2 - 1), (float)(1 - (r.Y + r.H) * 2));
            if (matrix >= 0) GLES20.GlUniformMatrix4fv(matrix, 1, false, texMatrix, 0);
        }

        private static int Compile(int type, string source)
        {
            var shader = GLES20.GlCreateShader(type);
            GLES20.GlShaderSource(shader, source);
            GLES20.GlCompileShader(shader);
            var status = new int[1];
            GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
            if (status[0] == 0) throw new InvalidOperationException("Shader compile failed: " + GLES20.GlGetShaderInfoLog(shader));
            return shader;
        }
    }
}
