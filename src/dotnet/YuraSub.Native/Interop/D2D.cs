using System;
using System.Runtime.InteropServices;

namespace YuraSub.Native.Interop;

// Minimal Direct2D / DirectWrite COM interop for text rendering with outline and shadow.

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_SIZE_F
{
    public float Width;
    public float Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_SIZE_U
{
    public uint Width;
    public uint Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_POINT_2F
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RECT_F
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_COLOR_F
{
    public float R;
    public float G;
    public float B;
    public float A;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RENDER_TARGET_PROPERTIES
{
    public int Type;
    public D2D1_PIXEL_FORMAT PixelFormat;
    public float DpiX;
    public float DpiY;
    public int Usage;
    public int MinLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_PIXEL_FORMAT
{
    public int Format;
    public int AlphaMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_HWND_RENDER_TARGET_PROPERTIES
{
    public IntPtr Hwnd;
    public D2D1_SIZE_U PixelSize;
    public int PresentOptions;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_TEXT_RANGE
{
    public uint StartPosition;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_ROUNDED_RECT
{
    public D2D1_RECT_F Rect;
    public float RadiusX;
    public float RadiusY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_STROKE_STYLE_PROPERTIES
{
    public int StartCap;
    public int EndCap;
    public int DashCap;
    public int LineJoin;
    public float MiterLimit;
    public int DashStyle;
    public float DashOffset;
}

internal static class D2DFactory
{
    private static readonly Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid IID_ID2D1HwndRenderTarget = new Guid("2cd90698-12e2-11dc-9fed-001143a055f9");
    private static readonly Guid IID_ID2D1SolidColorBrush = new Guid("2cd906a9-12e2-11dc-9fed-001143a055f9");
    private static readonly Guid IID_ID2D1StrokeStyle = new Guid("2cd906a7-12e2-11dc-9fed-001143a055f9");
    private static readonly Guid IID_IDWriteFactory = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    private static unsafe void* GetVTableFunction(IntPtr instance, int index)
    {
        return (*(void***)instance)[index];
    }

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int factoryType, ref Guid iid, IntPtr pFactoryOptions, out IntPtr ppIFactory);

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out IntPtr ppIFactory);

    internal static IntPtr CreateD2DFactory()
    {
        Guid iid = IID_ID2D1Factory;
        int hr = D2D1CreateFactory(0, ref iid, IntPtr.Zero, out IntPtr factory); // 0 = D2D1_FACTORY_TYPE_SINGLE_THREADED
        if (hr != 0) throw new InvalidOperationException($"D2D1CreateFactory failed: 0x{hr:X8}");
        return factory;
    }

    internal static IntPtr CreateDWriteFactory()
    {
        Guid iid = IID_IDWriteFactory;
        int hr = DWriteCreateFactory(0, ref iid, out IntPtr factory); // 0 = DWRITE_FACTORY_TYPE_SHARED
        if (hr != 0) throw new InvalidOperationException($"DWriteCreateFactory failed: 0x{hr:X8}");
        return factory;
    }

    // --- ID2D1Factory vtable ---
    // IUnknown: QueryInterface(0), AddRef(1), Release(2)
    // ID2D1Factory: ReloadSystemMetrics(3), GetDesktopDpi(4), CreateRectangleGeometry(5),
    //   CreateRoundedRectangleGeometry(6), CreateEllipseGeometry(7), CreateGeometryGroup(8),
    //   CreateTransformedGeometry(9), CreatePathGeometry(10), CreateStrokeStyle(11),
    //   CreateDrawingStateBlock(12), CreateWicBitmapRenderTarget(13), CreateHwndRenderTarget(14),
    //   CreateDxgiSurfaceRenderTarget(15), CreateDCRenderTarget(16)

    // --- ID2D1Factory vtable ---
    // IUnknown: QueryInterface(0), AddRef(1), Release(2)
    // ID2D1Factory: ReloadSystemMetrics(3), GetDesktopDpi(4), CreateRectangleGeometry(5),
    //   CreateRoundedRectangleGeometry(6), CreateEllipseGeometry(7), CreateGeometryGroup(8),
    //   CreateTransformedGeometry(9), CreatePathGeometry(10), CreateStrokeStyle(11),
    //   CreateDrawingStateBlock(12), CreateWicBitmapRenderTarget(13), CreateHwndRenderTarget(14),
    //   CreateDxgiSurfaceRenderTarget(15), CreateDCRenderTarget(16)

    internal static unsafe IntPtr CreateDCRenderTarget(IntPtr factory, D2D1_RENDER_TARGET_PROPERTIES props)
    {
        IntPtr dcrt;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_RENDER_TARGET_PROPERTIES*, IntPtr*, int>)GetVTableFunction(factory, 16);
        D2D1_RENDER_TARGET_PROPERTIES localProps = props;
        int hr = fn(factory, &localProps, &dcrt);
        if (hr != 0) throw new InvalidOperationException($"CreateDCRenderTarget failed: 0x{hr:X8}");
        return dcrt;
    }

    // --- ID2D1DCRenderTarget::BindDC ---
    // vtable: ID2D1RT(0-56) + ID2D1DCRT::BindDC(57)
    // Note: ID2D1BitmapRT::GetBitmap is NOT in the DCRT vtable because
    // ID2D1DCRenderTarget inherits directly from ID2D1RenderTarget in practice.
    internal static unsafe void BindDC(IntPtr dcrt, IntPtr hdc, ref Win32.RECT rect)
    {
        fixed (Win32.RECT* pRect = &rect)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32.RECT*, int>)GetVTableFunction(dcrt, 57);
            int hr = fn(dcrt, hdc, pRect);
            if (hr != 0) throw new InvalidOperationException($"BindDC failed: 0x{hr:X8}");
        }
    }

    internal static IntPtr CreateHwndRenderTarget(IntPtr factory, IntPtr hwnd, int width, int height)
    {
        unsafe
        {
            // Get desktop DPI
            float dpiX, dpiY;
            var getDpi = (delegate* unmanaged[Stdcall]<IntPtr, float*, float*, void>)GetVTableFunction(factory, 4);
            getDpi(factory, &dpiX, &dpiY);

            var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
            {
                Type = 0, // DEFAULT
                PixelFormat = new D2D1_PIXEL_FORMAT
                {
                    Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                    AlphaMode = 1, // D2D1_ALPHA_MODE_PREMULTIPLIED
                },
                DpiX = dpiX,
                DpiY = dpiY,
                Usage = 0,
                MinLevel = 0,
            };

            var size = new D2D1_SIZE_U { Width = (uint)width, Height = (uint)height };
            var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
            {
                Hwnd = hwnd,
                PixelSize = size,
                PresentOptions = 0,
            };

            var createHwndRt = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_RENDER_TARGET_PROPERTIES*, D2D1_HWND_RENDER_TARGET_PROPERTIES*, IntPtr*, int>)GetVTableFunction(factory, 14);
            IntPtr renderTarget;
            int hr = createHwndRt(factory, &rtProps, &hwndProps, &renderTarget);
            if (hr != 0) throw new InvalidOperationException($"CreateHwndRenderTarget failed: 0x{hr:X8}");
            return renderTarget;
        }
    }

    // --- ID2D1RenderTarget vtable ---
    // IUnknown: 0-2
    // ID2D1Resource: GetFactory(3)
    // ID2D1RenderTarget: CreateBitmap(4), CreateBitmapFromWicBitmap(5), CreateSharedBitmap(6),
    //   CreateBitmapBrush(7), CreateSolidColorBrush(8), CreateGradientStopCollection(9),
    //   CreateLinearGradientBrush(10), CreateRadialGradientBrush(11), CreateCompatibleRenderTarget(12),
    //   CreateLayer(13), CreateMesh(14), DrawLine(15), DrawRectangle(16), FillRectangle(17),
    //   DrawRoundedRectangle(18), FillRoundedRectangle(19), DrawEllipse(20), FillEllipse(21),
    //   DrawGeometry(22), FillGeometry(23), FillMesh(24), FillOpacityMask(25),
    //   DrawBitmap(26), DrawText(27), DrawTextLayout(28), DrawGlyphRun(29),
    //   SetTransform(30), GetTransform(31), SetAntialiasMode(32), GetAntialiasMode(33),
    //   SetTextAntialiasMode(34), GetTextAntialiasMode(35), SetTextRenderingParams(36),
    //   GetTextRenderingParams(37), SetTags(38), GetTags(39), PushLayer(40), PopLayer(41),
    //   Flush(42), SaveDrawingState(43), RestoreDrawingState(44), PushAxisAlignedClip(45),
    //   PopAxisAlignedClip(46), Clear(47), BeginDraw(48), EndDraw(49),
    //   GetPixelFormat(50), SetDpi(51), GetDpi(52), GetSize(53), GetPixelSize(54),
    //   GetMaximumBitmapSize(55), IsSupported(56)

    internal static void BeginDraw(IntPtr rt)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, void>)GetVTableFunction(rt, 48);
            fn(rt);
        }
    }

    internal static int EndDraw(IntPtr rt)
    {
        unsafe
        {
            ulong tag1, tag2;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)GetVTableFunction(rt, 49);
            return fn(rt, &tag1, &tag2);
        }
    }

    internal static void Clear(IntPtr rt, D2D1_COLOR_F color)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, void>)GetVTableFunction(rt, 47);
            fn(rt, &color);
        }
    }

    internal static IntPtr CreateSolidColorBrush(IntPtr rt, D2D1_COLOR_F color)
    {
        unsafe
        {
            IntPtr brush;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, IntPtr, IntPtr*, int>)GetVTableFunction(rt, 8);
            int hr = fn(rt, &color, IntPtr.Zero, &brush);
            if (hr != 0) throw new InvalidOperationException($"CreateSolidColorBrush failed: 0x{hr:X8}");
            return brush;
        }
    }

    internal static void DrawLine(IntPtr rt, D2D1_POINT_2F p0, D2D1_POINT_2F p1, IntPtr brush, float strokeWidth, IntPtr strokeStyle)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_POINT_2F, D2D1_POINT_2F, IntPtr, float, IntPtr, void>)GetVTableFunction(rt, 15);
            fn(rt, p0, p1, brush, strokeWidth, strokeStyle);
        }
    }

    internal static void FillRoundedRect(IntPtr rt, D2D1_ROUNDED_RECT rect, IntPtr brush)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_ROUNDED_RECT*, IntPtr, void>)GetVTableFunction(rt, 19);
            fn(rt, &rect, brush);
        }
    }

    internal static void DrawRoundedRect(IntPtr rt, D2D1_ROUNDED_RECT rect, IntPtr brush, float strokeWidth, IntPtr strokeStyle)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_ROUNDED_RECT*, IntPtr, float, IntPtr, void>)GetVTableFunction(rt, 18);
            fn(rt, &rect, brush, strokeWidth, strokeStyle);
        }
    }

    internal static void DrawRectangle(IntPtr rt, D2D1_RECT_F rect, IntPtr brush, float strokeWidth, IntPtr strokeStyle)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_RECT_F*, IntPtr, float, IntPtr, void>)GetVTableFunction(rt, 16);
            fn(rt, &rect, brush, strokeWidth, strokeStyle);
        }
    }

    internal static void FillRectangle(IntPtr rt, D2D1_RECT_F rect, IntPtr brush)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_RECT_F*, IntPtr, void>)GetVTableFunction(rt, 17);
            fn(rt, &rect, brush);
        }
    }

    // ID2D1RenderTarget::DrawEllipse vtable index = 20
    internal static void DrawEllipse(IntPtr rt, float cx, float cy, float rx, float ry, IntPtr brush, float strokeWidth, IntPtr strokeStyle)
    {
        unsafe
        {
            // D2D1_ELLIPSE = { D2D1_POINT_2F, float, float }
            var ellipse = stackalloc float[4]; // cx, cy, rx, ry
            ellipse[0] = cx; ellipse[1] = cy; ellipse[2] = rx; ellipse[3] = ry;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, float*, IntPtr, float, IntPtr, void>)GetVTableFunction(rt, 20);
            fn(rt, ellipse, brush, strokeWidth, strokeStyle);
        }
    }

    internal static void DrawTextLayout(IntPtr rt, float x, float y, IntPtr textLayout, IntPtr brush, int options)
    {
        unsafe
        {
            var origin = new D2D1_POINT_2F { X = x, Y = y };
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_POINT_2F, IntPtr, IntPtr, int, void>)GetVTableFunction(rt, 28);
            fn(rt, origin, textLayout, brush, options);
        }
    }

    internal static void SetTransform(IntPtr rt, ref System.Numerics.Matrix3x2 transform)
    {
        unsafe
        {
            fixed (System.Numerics.Matrix3x2* pTransform = &transform)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, System.Numerics.Matrix3x2*, void>)GetVTableFunction(rt, 30);
                fn(rt, pTransform);
            }
        }
    }

    internal static void SetTextAntialiasMode(IntPtr rt, int mode)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, void>)GetVTableFunction(rt, 34);
            fn(rt, mode);
        }
    }

    internal static D2D1_SIZE_F GetSize(IntPtr rt)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_SIZE_F>)GetVTableFunction(rt, 53);
            return fn(rt);
        }
    }

    internal static void Resize(IntPtr rt, D2D1_SIZE_U size)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_SIZE_U*, int>)GetVTableFunction(rt, 58);
            fn(rt, &size);
        }
    }

    // --- COM Release ---
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    internal static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.Release(comObject);
        }
    }
}

internal static class DWriteInterop
{
    private static unsafe void* GetVTableFunction(IntPtr instance, int index)
    {
        return (*(void***)instance)[index];
    }

    // --- IDWriteFactory vtable ---
    // IUnknown: 0-2
    // IDWriteFactory: GetSystemFontCollection(3), CreateCustomFontCollection(4),
    //   RegisterFontCollectionLoader(5), UnregisterFontCollectionLoader(6),
    //   CreateFontFileReference(7), CreateCustomFontFileReference(8),
    //   CreateFontFace(9), CreateRenderingParams(10), CreateMonitorRenderingParams(11),
    //   CreateCustomRenderingParams(12), RegisterFontFileLoader(13), UnregisterFontFileLoader(14),
    //   CreateTextFormat(15), CreateTypography(16), GetGdiInterop(17),
    //   CreateTextLayout(18), CreateGdiCompatibleTextLayout(19), CreateEllipsisTrimmingSign(20),
    //   CreateTextAnalyzer(21), CreateNumberSubstitution(22), CreateGlyphRunAnalysis(23)

    internal static IntPtr CreateTextFormat(IntPtr factory, string fontFamily, float fontSize, int weight, int style, int stretch)
    {
        unsafe
        {
            IntPtr textFormat;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int, int, int, float, char*, IntPtr*, int>)GetVTableFunction(factory, 15);
            string localeName = "en-us";
            fixed (char* pFamily = fontFamily)
            fixed (char* pLocale = localeName)
            {
                int hr = fn(factory, pFamily, IntPtr.Zero, weight, style, stretch, fontSize, pLocale, &textFormat);
                if (hr != 0) throw new InvalidOperationException($"CreateTextFormat failed: 0x{hr:X8}");
            }
            return textFormat;
        }
    }

    internal static IntPtr CreateTextLayout(IntPtr factory, string text, IntPtr textFormat, float maxWidth, float maxHeight)
    {
        unsafe
        {
            IntPtr textLayout;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, float, float, IntPtr*, int>)GetVTableFunction(factory, 18);
            fixed (char* pText = text)
            {
                int hr = fn(factory, pText, (uint)text.Length, textFormat, maxWidth, maxHeight, &textLayout);
                if (hr != 0) throw new InvalidOperationException($"CreateTextLayout failed: 0x{hr:X8}");
            }
            return textLayout;
        }
    }

    // --- IDWriteTextFormat vtable ---
    // IUnknown: 0-2
    // IDWriteTextFormat: SetTextAlignment(3), SetParagraphAlignment(4), SetWordWrapping(5),
    //   SetReadingDirection(6), SetFlowDirection(7), SetIncrementalTabStop(8),
    //   SetTrimming(9), SetLineSpacing(10), GetTextAlignment(11), GetParagraphAlignment(12),
    //   GetWordWrapping(13), GetReadingDirection(14), GetFlowDirection(15),
    //   GetIncrementalTabStop(16), GetTrimming(17), GetLineSpacing(18),
    //   GetFontCollection(19), GetFontFamilyNameLength(20), GetFontFamilyName(21),
    //   GetFontWeight(22), GetFontStyle(23), GetFontStretch(24), GetFontSize(25),
    //   GetLocaleNameLength(26), GetLocaleName(27)

    internal static void SetTextAlignment(IntPtr textFormat, int alignment) // 0=LEADING, 1=TRAILING, 2=CENTER
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)GetVTableFunction(textFormat, 3);
            fn(textFormat, alignment);
        }
    }

    internal static void SetParagraphAlignment(IntPtr textFormat, int alignment) // 0=NEAR, 1=FAR, 2=CENTER
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)GetVTableFunction(textFormat, 4);
            fn(textFormat, alignment);
        }
    }

    internal static void SetWordWrapping(IntPtr textFormat, int wrapping) // 0=WRAP, 1=NO_WRAP, 2=EMERGENCY_BREAK, 3=WHOLE_WORD, 4=CHARACTER
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)GetVTableFunction(textFormat, 5);
            fn(textFormat, wrapping);
        }
    }

    // --- IDWriteTextLayout vtable ---
    // IUnknown: 0-2
    // IDWriteTextFormat: 3-27
    // IDWriteTextLayout: SetMaxWidth(28), SetMaxHeight(29), SetFontCollection(30),
    //   SetFontFamilyName(31), SetFontWeight(32), SetFontStyle(33), SetFontStretch(34),
    //   SetFontSize(35), SetUnderline(36), SetStrikethrough(37), SetDrawingEffect(38),
    //   SetInlineObject(39), SetTypography(40), SetLocaleName(41),
    //   GetMaxWidth(42), GetMaxHeight(43), ...
    //   Draw(58), GetLineMetrics(59), GetMetrics(60), GetOverhangMetrics(61),
    //   GetClusterMetrics(62), DetermineMinWidth(63)

    internal static void SetFontSize(IntPtr textLayout, float fontSize, DWRITE_TEXT_RANGE range)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, float, DWRITE_TEXT_RANGE, int>)GetVTableFunction(textLayout, 35);
            fn(textLayout, fontSize, range);
        }
    }

    internal static void SetFontWeight(IntPtr textLayout, int weight, DWRITE_TEXT_RANGE range)
    {
        unsafe
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, int>)GetVTableFunction(textLayout, 32);
            fn(textLayout, weight, range);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DWRITE_TEXT_METRICS
    {
        public float Left;
        public float Top;
        public float Width;
        public float WidthIncludingTrailingWhitespace;
        public float Height;
        public float LayoutWidth;
        public float LayoutHeight;
        public uint MaxBidiReorderingDepth;
        public uint LineCount;
    }

    internal static DWRITE_TEXT_METRICS GetMetrics(IntPtr textLayout)
    {
        unsafe
        {
            DWRITE_TEXT_METRICS metrics;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, DWRITE_TEXT_METRICS*, int>)GetVTableFunction(textLayout, 60);
            fn(textLayout, &metrics);
            return metrics;
        }
    }

    // DWrite font weight constants
    public const int DWRITE_FONT_WEIGHT_NORMAL = 400;
    public const int DWRITE_FONT_WEIGHT_MEDIUM = 500;
    public const int DWRITE_FONT_WEIGHT_BOLD = 700;
    public const int DWRITE_FONT_STYLE_NORMAL = 0;
    public const int DWRITE_FONT_STRETCH_NORMAL = 5;
    public const int DWRITE_WORD_WRAPPING_WRAP = 0;
    public const int DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    public const int DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
}
