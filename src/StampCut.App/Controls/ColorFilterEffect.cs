using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StampCut.Core.Models;

namespace StampCut.App.Controls;

/// <summary>
/// Фильтры картинки (тени, светлые участки, насыщенность, яркость, контраст) в реальном времени на видеокарте.
/// Шейдер компилируется на машине пользователя стандартным d3dcompiler_47 из Windows; если это не удалось — Available=false,
/// и приложение показывает кадр с фильтрами через ffmpeg только на паузе.
/// </summary>
public sealed class ColorFilterEffect : ShaderEffect
{
    private const string Hlsl = """
        sampler2D input : register(s0);
        float shadows : register(c0);
        float highlights : register(c1);
        float saturation : register(c2);
        float brightness : register(c3);
        float contrast : register(c4);

        // кривая Катмулла-Рома через точки (0,0) (0.25,y1) (0.5,0.5) (0.75,y2) (1,1) — как curves в ffmpeg
        float curve(float x)
        {
            float y1 = 0.25 + shadows * 0.15;
            float y2 = 0.75 - highlights * 0.15;
            float ys[5] = { 0.0, y1, 0.5, y2, 1.0 };
            float fx = saturate(x) * 4.0;
            int i = (int)floor(min(fx, 3.0));
            float t = fx - i;
            float p0 = ys[max(i - 1, 0)], p1 = ys[i], p2 = ys[min(i + 1, 4)], p3 = ys[min(i + 2, 4)];
            if (i == 0) p0 = 2.0 * p1 - p2;
            if (i == 3) p3 = 2.0 * p2 - p1;
            float t2 = t * t, t3 = t2 * t;
            return saturate(0.5 * ((2.0 * p1) + (-p0 + p2) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3));
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 c = tex2D(input, uv);
            float a = max(c.a, 0.0001);
            float3 rgb = c.rgb / a;
            rgb = float3(curve(rgb.r), curve(rgb.g), curve(rgb.b));
            float lum = dot(rgb, float3(0.299, 0.587, 0.114));
            rgb = lerp(lum.xxx, rgb, saturation);
            rgb = (rgb - 0.5) * contrast + 0.5 + brightness;
            rgb = saturate(rgb);
            return float4(rgb * c.a, c.a);
        }
        """;

    private static readonly Lazy<PixelShader?> Shader = new(Compile);
    /// <summary>Шейдер скомпилирован и видеокарта (не удалённый рабочий стол, не базовый адаптер) умеет ps_3_0 — иначе WPF просто не нарисует элемент.</summary>
    public static bool Available => Shader.Value is not null && RenderCapability.IsPixelShaderVersionSupported(3, 0);

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(ColorFilterEffect), 0);
    public static readonly DependencyProperty ShadowsProperty = DependencyProperty.Register("Shadows", typeof(double), typeof(ColorFilterEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty HighlightsProperty = DependencyProperty.Register("Highlights", typeof(double), typeof(ColorFilterEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register("Saturation", typeof(double), typeof(ColorFilterEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register("Brightness", typeof(double), typeof(ColorFilterEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty ContrastProperty = DependencyProperty.Register("Contrast", typeof(double), typeof(ColorFilterEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Shadows { get => (double)GetValue(ShadowsProperty); set => SetValue(ShadowsProperty, value); }
    public double Highlights { get => (double)GetValue(HighlightsProperty); set => SetValue(HighlightsProperty, value); }
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public double Contrast { get => (double)GetValue(ContrastProperty); set => SetValue(ContrastProperty, value); }

    public ColorFilterEffect()
    {
        PixelShader = Shader.Value ?? throw new InvalidOperationException("шейдер недоступен");
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ShadowsProperty); UpdateShaderValue(HighlightsProperty); UpdateShaderValue(SaturationProperty);
        UpdateShaderValue(BrightnessProperty); UpdateShaderValue(ContrastProperty);
    }

    /// <summary>Те же формулы, что в VideoFilters.ToFfmpeg: проценты → параметры шейдера.</summary>
    public void Apply(VideoFilters f)
    {
        Shadows = Math.Clamp(f.Shadows, -100, 100) / 100.0;
        Highlights = Math.Clamp(f.Highlights, -100, 100) / 100.0;
        Saturation = Math.Clamp(f.Saturation, 0, 300) / 100.0;
        Brightness = Math.Clamp(f.Brightness, -100, 100) / 100.0 * 0.3;
        Contrast = 1 + Math.Clamp(f.Contrast, -100, 100) / 100.0 * 0.5;
    }

    // ------------------------------------------------------------ компиляция HLSL через d3dcompiler_47.dll

    private static PixelShader? Compile()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var src = Encoding.ASCII.GetBytes(Hlsl);
            int hr = D3DCompile(src, (nuint)src.Length, "colorfilter.hlsl", IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 0, 0, out var code, out var errors);
            if (hr < 0 || code is null)
            {
                var msg = errors is null ? "" : Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize());
                Services.Logger.Warn($"Шейдер фильтров не скомпилировался (0x{hr:X8}): {msg}");
                return null;
            }
            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            var ps = new PixelShader { ShaderRenderMode = ShaderRenderMode.Auto };
            ps.SetStreamSource(new MemoryStream(bytes));
            ps.Freeze();
            return ps;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn("Шейдер фильтров недоступен: " + ex.Message);
            return null;
        }
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] srcData, nuint srcSize, string sourceName, IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);
}
