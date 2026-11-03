namespace StampCut.Core.Models;

public enum FitMode { Keep, PadBlack, PadBlur, Crop }
public enum HardwareEncoder { Auto, None, Nvenc, Qsv, Amf, MediaFoundation }

public sealed class ExportPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }

    /// <summary>Режим Machete: копирование потоков без перекодирования.</summary>
    public bool StreamCopy { get; set; }

    public int? MaxLongSide { get; set; } = 1920;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public FitMode Fit { get; set; } = FitMode.Keep;
    public int MaxFps { get; set; } = 30;
    /// <summary>Авто: CRF 18 и потолок битрейта по исходнику (качество не теряется, файл не раздувается). Иначе — фиксированный Crf.</summary>
    public bool AutoQuality { get; set; } = true;
    public int Crf { get; set; } = 21;
    public string X264Preset { get; set; } = "veryfast";
    public string Profile { get; set; } = "high";
    /// <summary>Уровень H.264: "auto" — по размеру кадра и частоте (4.1 для 1080p30, 4.2 для 1080p60, 5.1 для 4K).</summary>
    public string Level { get; set; } = "auto";
    public bool HdrToSdr { get; set; } = true;
    public HardwareEncoder HardwareEncoder { get; set; } = HardwareEncoder.Auto;

    public int AudioBitrateKbps { get; set; } = 128;
    public int SampleRate { get; set; } = 48000;
    public bool LoudnessNormalize { get; set; }

    public bool FastStart { get; set; } = true;
    public string OutputSuffix { get; set; } = "_tg";

    public bool HasFixedSize => Width is > 0 && Height is > 0;

    public ExportPreset Clone() => (ExportPreset)MemberwiseClone();
}
