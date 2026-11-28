using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIKernel.Contracts.Images;
using AIKernel.Providers.Flux;
using AIKernel.Providers.Image;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace TheoriaCast.Visual.Api;

internal static class EditRecipes
{
    internal const string ImageProvider = "AIKernel.Providers.Image";
    private const string LegacyImageProviderVersion = "0.2.0";
    private const string JpegOrientationKey = "jpegOrientation";
    private const string ApplyExif = "OpenCV EXIF; lossless PNG for Image Provider";
    internal const string InspectionEngineVersion = "2";
    internal static string ImageProviderVersion => VersionOf(typeof(ImageEditProvider));
    private static string VersionOf(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? type.Assembly.GetName().Version?.ToString() ?? throw new InvalidOperationException("Assembly version unavailable.");

    public static string CpuStylizationStyle(string touch) => touch == "manga" ? "pencil" : touch;

    private static readonly Dictionary<string, string> FluxFiles = new()
    {
        ["flux-2-klein-4b-Q4_0.gguf"] = "d1023499ef3f2f82ff7c50e6778495195c1b6cc34835741778868428111f9ff4",
        ["Qwen3-4B-Q4_K_M.gguf"] = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
        ["flux2-vae.safetensors"] = "ca70d2202afe6415bdbcb8793ba8cd99fd159cfe6192381504d6c4d3036e0f04"
    };

    public static bool ModelPresent(string? directory) =>
        directory is not null && FluxFiles.Keys.All(name => File.Exists(Path.Combine(directory, name)));

    public static ProcessingEngine Engine(EditSettings settings, string hardware, string? inspectionVersion = null,
        bool normalizeJpeg = true)
    {
        var algorithm = settings.Operation switch
        {
            "resize" when settings.FitMode == "pad" => "OpenCV contain with padding",
            "resize" => "OpenCV center crop",
            "colorMatch" => "OpenCV Lab color transfer",
            "inspect" when settings.InspectionMode == "cosmos" => "OpenCV / NVIDIA Cosmos 3 Edge",
            "inspect" => "OpenCV visual inspection",
            "stylize" when hardware == "cpu" => "OpenCV NPR",
            "background" when settings.Background != "generated" => "BiRefNet_lite 512 FP32",
            _ => "FLUX.2 [klein] 4B"
        };
        var libs = new Dictionary<string, string>
        {
            ["OpenCV"] = Cv2.GetVersionString() ?? throw new InvalidOperationException("OpenCV version unavailable."),
            ["OpenCvSharp"] = VersionOf(typeof(Cv2))
        };
        var models = new Dictionary<string, string>();
        var parameters = new Dictionary<string, string>
        {
            [JpegOrientationKey] = normalizeJpeg ? ApplyExif : "stored pixel orientation"
        };
        if (settings.Operation == "resize")
        {
            parameters["anchor"] = settings.FitMode == "pad" ? "center of safe area" : "center";
            parameters["interpolation"] = "area downsample / linear upsample";
        }
        if (settings.Operation == "colorMatch")
            parameters["method"] = "Lab mean and standard deviation with luminance curve";
        var version = "1";
        if (settings.Operation == "inspect")
        {
            version = inspectionVersion ?? InspectionEngineVersion;
            parameters["pixelChecks"] = "alpha occupancy, highlight clipping, Canny density";
            if (version == InspectionEngineVersion)
                VisualProcessing.DescribeMatteTopology(parameters);
            else if (version != "1")
                throw new VisualConflict("固定レシピに記録された検品方式を利用できません。");
        }
        if (settings.Operation == "background")
        {
            libs["ONNX Runtime"] = OrtEnv.Instance().GetVersionString();
            libs[ImageProvider] = ImageProviderVersion;
            models["birefnet-lite-512.onnx"] = ImageEditProvider.BackgroundModelSha256;
            parameters["matte"] = "512×512 FP32, soft alpha";
        }
        if (algorithm == "OpenCV NPR")
        {
            libs[ImageProvider] = ImageProviderVersion;
            var style = CpuStylizationStyle(settings.Touch!);
            parameters["style"] = style;
            parameters["OpenCV photo"] = style switch
            {
                "pencil" => "pencilSketch, sigma_s=60, sigma_r=0.07, shade_factor=0.02",
                "detail" => "detailEnhance, sigma_s=10, sigma_r=0.15",
                _ => "stylization, sigma_s=60, sigma_r=0.45"
            };
        }
        if (algorithm == "FLUX.2 [klein] 4B")
        {
            var provider = hardware == "cpu" ? typeof(FluxImageProvider) : typeof(FluxCudaImageProvider);
            libs[provider.Assembly.GetName().Name!] = VersionOf(provider);
            parameters["sampler"] = "Euler";
            parameters["steps"] = "4";
            parameters["cfg"] = "1";
            parameters["negativePrompt"] = "";
            foreach (var item in FluxFiles) models.Add(item.Key, item.Value);
        }
        return new(algorithm, version, hardware, libs, models, parameters);
    }

    // Variations can change the concrete OpenCV operation within the same frozen provider.
    public static bool SameRuntime(ProcessingEngine a, ProcessingEngine b) =>
        a.Hardware == b.Hardware && a.Algorithm == b.Algorithm && a.Version == b.Version
        && SameMap(a.Libraries, b.Libraries) && SameMap(a.Models, b.Models);

    public static bool Matches(ProcessingEngine recorded, ProcessingEngine current, EditSettings settings)
    {
        if (recorded.FixedParameters.ContainsKey(JpegOrientationKey))
            return SameMap(recorded.FixedParameters, current.FixedParameters) && SameRuntime(recorded, current);

        // Old recipes omitted the JPEG policy and recorded release strings instead of loaded versions.
        var parameters = new Dictionary<string, string>(current.FixedParameters);
        parameters.Remove(JpegOrientationKey);
        var libraries = current.Libraries.ToDictionary(pair => pair.Key, pair => pair.Value.Split('+', 2)[0]);
        if (libraries["OpenCvSharp"] == "4.13.0.20261107-beta")
            libraries["OpenCvSharp"] = "4.13.0.20261107";
        if (libraries.GetValueOrDefault("AIKernel.Providers.Flux") == "0.1.5" ||
            libraries.GetValueOrDefault("AIKernel.Providers.Flux.Cuda") == "0.1.5")
            libraries["FLUX ABI"] = "1";
        // 0.3.0 preserves 0.2.0 behavior while the new background edge options are inactive.
        if (UsesLegacyImageProvider(recorded) && !RefinesEdges(settings) &&
            libraries.GetValueOrDefault(ImageProvider) == "0.3.0")
            libraries[ImageProvider] = LegacyImageProviderVersion;
        return SameMap(recorded.FixedParameters, parameters) && SameRuntime(recorded, current with { Libraries = libraries });
    }

    public static bool NormalizesJpeg(ProcessingEngine engine) =>
        engine.FixedParameters.GetValueOrDefault(JpegOrientationKey) == ApplyExif;

    public static bool UsesLegacyImageProvider(ProcessingEngine engine) =>
        engine.Libraries.GetValueOrDefault(ImageProvider) == LegacyImageProviderVersion;

    private static bool RefinesEdges(EditSettings settings) =>
        settings is { MaskRefineRadius: > 0 } or { MaskErodePx: > 0 } or { DecontaminateEdge: true };

    public static EditSettings WithMattingDefaults(EditSettings settings) => settings.Operation == "background"
        ? settings with
        {
            MaskRefineRadius = settings.MaskRefineRadius ?? 0,
            MaskErodePx = settings.MaskErodePx ?? 0,
            DecontaminateEdge = settings.DecontaminateEdge ?? false
        }
        : settings;

    private static bool SameMap(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(item => second.TryGetValue(item.Key, out var value) && value == item.Value);

    public static EditSettings Normalize(EditSettings settings, string hardware)
    {
        if (settings is null) throw new ArgumentException("編集内容を指定してください。");
        var op = settings.Operation;
        var generated = op == "background" && settings.Background == "generated";
        var usesFlux = generated || op == "stylize" && hardware != "cpu";
        if (op != "stylize" && (settings.Touch is not null || settings.Strength is not null)
            || op != "resize" && (settings.Width is not null || settings.Height is not null)
            || op != "background" && (settings.Background is not null || settings.MaskRefineRadius is not null ||
                settings.MaskErodePx is not null || settings.DecontaminateEdge is not null)
            || settings.Background != "solid" && settings.Color is not null
            || settings.Background != "image" && settings.BackgroundAsset is not null
            || !generated && settings.Prompt is not null
            || op != "resize" && (settings.FitMode is not null || settings.PadStyle is not null ||
                settings.PadColor is not null || settings.MarginOffsets is not null || settings.SafeAreaMargin is not null)
            || op != "colorMatch" && (settings.ReferenceAsset is not null || settings.ColorMatchStrength is not null)
            || op != "inspect" && settings.InspectionMode is not null
            || !usesFlux && (settings.Seed is not null || settings.Resolution is not null))
            throw new ArgumentException("指定されたパラメータは、この編集方法または処理先では使えません。");
        if (settings.MaskRefineRadius is < 0 or > 32 || settings.MaskErodePx is < 0 or > 2)
            throw new ArgumentException("エッジ補正の半径は0〜32px、収縮幅は0〜2pxで指定してください。");
        var normalized = op switch
        {
            "resize" when settings.Width is > 0 and <= 8192 && settings.Height is > 0 and <= 8192
                && (long)settings.Width.Value * settings.Height.Value <= 4 * 1024 * 1024 => NormalizeResize(settings),
            "colorMatch" when settings.ReferenceAsset is not null && (settings.ColorMatchStrength ?? 1) is >= 0 and <= 1 =>
                settings with { ReferenceAsset=VisualStore.CheckRef(settings.ReferenceAsset), ColorMatchStrength=settings.ColorMatchStrength??1 },
            "inspect" when (settings.InspectionMode ?? "pixels") is "pixels" or "cosmos" =>
                settings with { InspectionMode = settings.InspectionMode ?? "pixels" },
            "stylize" when (settings.Touch ?? "illustration") is "illustration" or "manga" or "pencil" or "detail"
                && (settings.Strength ?? 1) is >= 0 and <= 1 =>
                settings with { Touch = settings.Touch ?? "illustration", Strength = settings.Strength ?? 1 },
            "background" => NormalizeBackground(settings),
            _ => throw new ArgumentException("編集方法、出力サイズ、タッチまたは強さを確認してください。")
        };
        if (!usesFlux) return normalized;
        if (settings.Resolution is int resolution && (resolution is < 128 or > 1024 || resolution % 16 != 0))
            throw new ArgumentException("モデル解像度は128〜1024の範囲で16の倍数にしてください。");
        if (settings.Seed is < 0)
            throw new ArgumentException("再実行できるよう、シードは0以上の整数を指定してください。");
        return normalized with { Seed = settings.Seed ?? 42, Resolution = settings.Resolution ?? 512 };
    }

    private static EditSettings NormalizeResize(EditSettings settings)
    {
        if (settings.FitMode is null or "crop")
        {
            if (settings.PadStyle is not null || settings.PadColor is not null || settings.MarginOffsets is not null ||
                settings.SafeAreaMargin is not null)
                throw new ArgumentException("余白の設定には pad を指定してください。");
            return settings;
        }
        if (settings.FitMode!="pad" || (settings.PadStyle??"solid") is not ("solid" or "edge" or "blur"))
            throw new ArgumentException("フィット方法と余白の種類を確認してください。");
        var style=settings.PadStyle??"solid";
        var color=style=="solid"?settings.PadColor??"#f5f2eb":null;
        if (color is not null && (color.Length!=7 || color[0]!='#' ||
            !int.TryParse(color.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out _)) ||
            style!="solid" && settings.PadColor is not null)
            throw new ArgumentException("単色余白は #RRGGBB 形式で指定してください。");
        if (settings.MarginOffsets is { } offsets && settings.SafeAreaMargin is { } safeArea && offsets != safeArea)
            throw new ArgumentException("安全余白の値が一致しません。");
        var margin=settings.MarginOffsets??settings.SafeAreaMargin??new();
        VisualImages.SafeArea(settings.Width!.Value,settings.Height!.Value,margin);
        return settings with {PadStyle=style,PadColor=color?.ToLowerInvariant(),MarginOffsets=margin,SafeAreaMargin=null};
    }

    private static EditSettings NormalizeBackground(EditSettings settings) => settings.Background switch
    {
        null or "transparent" => settings with { Background = "transparent" },
        "solid" when settings.Color is { Length: 7 } color && color[0] == '#' &&
            int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _) =>
            settings with { Color = color.ToLowerInvariant() },
        "image" when settings.BackgroundAsset is not null => settings with { BackgroundAsset = VisualStore.CheckRef(settings.BackgroundAsset) },
        "generated" when settings.Prompt is { Length: <= 1000 } prompt && !string.IsNullOrWhiteSpace(prompt) =>
            settings with { Prompt = prompt.Trim() },
        _ => throw new ArgumentException("背景の種類、色、参照素材または生成指示を確認してください。")
    };

    public static EditSettings Change(EditSettings current, RecipeChange change, string hardware)
    {
        var value = change.Value;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ArgumentException("差分の値を指定してください。");
        EditSettings changed;
        try
        {
            changed = change.Parameter switch
            {
                "touch" => current with { Touch = value.GetString() },
                "strength" => current with { Strength = value.GetDouble() },
                "width" => current with { Width = value.GetInt32() },
                "height" => current with { Height = value.GetInt32() },
                "targetResolution" when current.Operation=="resize" =>
                    value.Deserialize<ImageSize>(JsonSerializerOptions.Web) is {} size
                        ? current with {Width=size.Width,Height=size.Height} : throw new ArgumentException(),
                "fitMode" when value.GetString()=="crop" =>
                    current with { FitMode="crop",PadStyle=null,PadColor=null,MarginOffsets=null },
                "fitMode" => current with { FitMode=value.GetString() },
                "padStyle" => current with { PadStyle=value.GetString(),
                    PadColor=value.GetString()=="solid"?current.PadColor:null },
                "padColor" => current with { PadColor=value.GetString() },
                "marginOffsets" or "safeAreaMargin" => current with { MarginOffsets=value.Deserialize<MarginOffsets>(JsonSerializerOptions.Web) },
                "referenceAsset" => current with { ReferenceAsset=value.Deserialize<AssetRef>(JsonSerializerOptions.Web) },
                "colorMatchStrength" => current with { ColorMatchStrength=value.GetDouble() },
                "inspectionMode" => current with { InspectionMode = value.GetString() },
                "color" => current with { Color = value.GetString() },
                "backgroundAsset" => current with { BackgroundAsset = value.Deserialize<AssetRef>(JsonSerializerOptions.Web) },
                "maskRefineRadius" => current with { MaskRefineRadius = value.GetInt32() },
                "maskErodePx" => current with { MaskErodePx = value.GetInt32() },
                "decontaminateEdge" => current with { DecontaminateEdge = value.GetBoolean() },
                "prompt" => current with { Prompt = value.GetString() },
                "seed" => current with { Seed = value.GetInt32() },
                "resolution" => current with { Resolution = value.GetInt32() },
                _ => throw new ArgumentException("元の処理で使用するパラメータを1つ指定してください。")
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            throw new ArgumentException("差分の値の形式を確認してください。");
        }
        changed = Normalize(changed, hardware);
        if (changed == current) throw new ArgumentException("元のレシピと異なる値を指定してください。");
        return changed;
    }

    public static string? EffectivePrompt(EditSettings settings, ProcessingEngine engine)
    {
        if (engine.Algorithm != "FLUX.2 [klein] 4B") return null;
        if (settings.Operation == "background")
            return "Replace the entire image with a background plate, with no foreground subject. " + settings.Prompt;
        return settings.Touch switch
        {
            "manga" => "Turn this photo into a manga panel with expressive ink lines and screen tones. Preserve the subject, composition and framing.",
            "pencil" => "Turn this photo into a hand-drawn graphite pencil illustration. Preserve the subject, composition and framing.",
            "detail" => "Create a finely detailed painted illustration from this photo. Preserve the subject, composition and framing.",
            _ => "Turn this photo into a hand-painted illustration with clean outlines and natural colors. Preserve the subject, composition and framing."
        };
    }
}

internal sealed partial class VisualProcessing : IDisposable
{
    private readonly ImageEditProvider editor;
    private readonly FluxCudaImageProvider? cuda;
    private readonly FluxImageProvider? cpuFlux;
    private readonly SemaphoreSlim gpuGate = new(1, 1);
    public bool HasMatting { get; }
    public bool HasGpu => cuda is not null;
    public bool HasCpuFlux => cpuFlux is not null;
    public double GpuSecondsAt256 { get; }
    public double CpuFluxSecondsAt256 { get; }

    public VisualProcessing(IConfiguration configuration, string data)
    {
        ConfigureInspection(configuration);
        GpuSecondsAt256 = Seconds(configuration, "Visual:GpuSecondsAt256", 60);
        CpuFluxSecondsAt256 = Seconds(configuration, "Visual:CpuFluxSecondsAt256", 180);
        var matte = configuration["Visual:MattingModel"];
        HasMatting = File.Exists(matte);
        editor = new ImageEditProvider(matte);
        var directory = configuration["Visual:FluxModels"];
        if (!EditRecipes.ModelPresent(directory)) return;
        var device = configuration.GetValue("Visual:CudaDevice", 0);
        cpuFlux = new FluxImageProvider(directory!, Path.Combine(data, "flux-cpu"));
        if (configuration.GetValue("Visual:EnableGpu", true) && DetectCuda(device))
            cuda = new FluxCudaImageProvider(directory!, Path.Combine(data, "flux-cuda"), deviceOrdinal: device);
    }

    private static double Seconds(IConfiguration configuration, string key, double fallback)
    {
        var value = configuration.GetValue(key, fallback);
        return double.IsFinite(value) && value is >= 1 and <= 3600 ? value
            : throw new ArgumentException("所要時間には1〜3600秒の値を設定してください。");
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetDeviceCount(out int count);

    private static bool DetectCuda(int device)
    {
        if (!OperatingSystem.IsWindows() || device < 0 ||
            !NativeLibrary.TryLoad("cublas64_13.dll", out var blas)) return false;
        NativeLibrary.Free(blas);
        if (!NativeLibrary.TryLoad("cudart64_13.dll", out var runtime)) return false;
        try
        {
            if (!NativeLibrary.TryGetExport(runtime, "cudaGetDeviceCount", out var address)) return false;
            var getCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(address);
            return getCount(out var count) == 0 && device < count;
        }
        finally { NativeLibrary.Free(runtime); }
    }

    public bool Supports(EditSettings settings, string hardware) =>
        (settings.Operation != "background" || HasMatting)
        && (settings.InspectionMode != "cosmos" || HasCosmos)
        && (hardware == "cpu" ? settings.Background != "generated" || HasCpuFlux : HasGpu);

    public async Task<byte[]> Run(VisualRecipe recipe, byte[] bytes, byte[]? background, CancellationToken ct)
    {
        var settings = recipe.Settings;
        var hardware = recipe.Engine.Hardware;
        var normalizeJpeg = EditRecipes.NormalizesJpeg(recipe.Engine);
        if (!Supports(settings, hardware) ||
            !EditRecipes.Matches(recipe.Engine, EditRecipes.Engine(settings, hardware, normalizeJpeg: normalizeJpeg), settings))
            throw new VisualConflict("この計算資源で固定レシピを実行できません。");
        if (VisualStore.Hash(bytes) != recipe.Input.Sha256)
            throw new InvalidDataException("入力素材の SHA-256 が一致しません。");
        bool gpu = hardware is "gpu" or "cloud-gpu";
        if (gpu) await gpuGate.WaitAsync(ct);
        try
        {
            using var source = VisualImages.Decode(bytes, normalizeJpeg);
            if (settings.Operation == "stylize" && !gpu)
            {
                var providerInput = VisualImages.ProviderInput(bytes, source, normalizeJpeg);
                var result = await editor.StylizeAsync(providerInput, EditRecipes.CpuStylizationStyle(settings.Touch!),
                    new ImageStylizationOptions(settings.Strength!.Value), ct);
                return result.ToArray();
            }

            using var image = settings.Operation switch
            {
                "resize" when settings.FitMode=="pad" => VisualImages.Pad(source,settings),
                "resize" => VisualImages.Crop(source, settings.Width!.Value, settings.Height!.Value),
                "colorMatch" => VisualImages.MatchColor(source,background!,settings.ColorMatchStrength!.Value,normalizeJpeg),
                "stylize" => await StylizeWithFlux(settings, recipe.EffectivePrompt!, source, ct),
                "background" => await Background(settings, recipe.EffectivePrompt, gpu,
                    VisualImages.ProviderInput(bytes, source, normalizeJpeg), source, background, normalizeJpeg, ct),
                _ => throw new ArgumentException("未対応の編集です。")
            };
            ct.ThrowIfCancellationRequested();
            return VisualImages.Encode(image);
        }
        finally { if (gpu) gpuGate.Release(); }
    }

    private async Task<Mat> StylizeWithFlux(EditSettings settings, string prompt, Mat source, CancellationToken ct)
    {
        using var edited = await Flux(source, settings, prompt, true, ct);
        return VisualImages.Blend(source, edited, settings.Strength!.Value);
    }

    private async Task<Mat> Background(EditSettings settings, string? prompt, bool gpu, byte[] input, Mat source,
        byte[]? background, bool normalizeJpeg, CancellationToken ct)
    {
        var options = new ImageMattingOptions(settings.MaskRefineRadius ?? 0, settings.MaskErodePx ?? 0,
            settings.DecontaminateEdge ?? false);
        var cutout = await editor.RemoveBackgroundAsync(input, options, ct);
        var foreground = VisualImages.Decode(cutout.ToArray());
        if (foreground.Size() != source.Size() || foreground.Type() != MatType.CV_8UC4)
        {
            foreground.Dispose();
            throw new InvalidDataException("背景除去の出力の寸法またはチャンネルが入力と一致しません。");
        }
        if (settings.Background == "transparent") return foreground;
        using (foreground)
        {
            using var layer = settings.Background switch
            {
                "solid" => Solid(source, settings.Color!),
                "image" => CustomBackground(background!, source, normalizeJpeg),
                "generated" => await Flux(source, settings, prompt!, gpu, ct),
                _ => throw new ArgumentException("未対応の背景です。")
            };
            using var rgba = VisualImages.ToBgra(layer);
            return VisualImages.Composite(foreground, rgba);
        }
    }

    private static Mat Solid(Mat source, string color)
    {
        var hex = int.Parse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new Mat(source.Rows, source.Cols, MatType.CV_8UC3, new Scalar(hex & 255, (hex >> 8) & 255, hex >> 16));
    }

    private static Mat CustomBackground(byte[] bytes, Mat source, bool normalizeJpeg)
    {
        using var layer = VisualImages.Decode(bytes, normalizeJpeg);
        return VisualImages.Crop(layer, source.Width, source.Height);
    }

    private async Task<Mat> Flux(Mat source, EditSettings settings, string prompt, bool gpu, CancellationToken ct)
    {
        var resolution = settings.Resolution!.Value;
        var scale = (double)resolution / Math.Max(source.Width, source.Height);
        int Dimension(int pixels) => Math.Clamp((int)Math.Round(pixels * scale / 16) * 16, 128, resolution);
        var width = Dimension(source.Width);
        var height = Dimension(source.Height);
        using var rgba = VisualImages.ToBgra(source);
        using var rgb = new Mat();
        using var small = new Mat();
        Cv2.CvtColor(rgba, rgb, ColorConversionCodes.BGRA2RGB);
        Cv2.Resize(rgb, small, new Size(width, height), interpolation: InterpolationFlags.Area);
        var input = new byte[width * height * 3];
        Marshal.Copy(small.Data, input, 0, input.Length);
        IImagePromptEditProvider runtime = gpu ? cuda! : cpuFlux!;
        var result = await runtime.EditAsync(input, width, height, prompt, settings.Seed!.Value, ct);
        if (result.Length != input.Length) throw new InvalidDataException("GPU の出力サイズが一致しません。");
        using var modelOutput = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(result.ToArray(), 0, modelOutput.Data, input.Length);
        using var bgr = new Mat();
        Cv2.CvtColor(modelOutput, bgr, ColorConversionCodes.RGB2BGR);
        var output = new Mat();
        Cv2.Resize(bgr, output, source.Size(), interpolation: InterpolationFlags.Linear);
        return output;
    }

    public void Dispose()
    {
        editor.Dispose();
        cpuFlux?.Dispose();
        cuda?.Dispose();
        if (cosmos?.IsValueCreated == true) cosmos.Value.Dispose();
        gpuGate.Dispose();
    }
}
