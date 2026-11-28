using OpenCvSharp;

namespace TheoriaCast.Visual.Api;

internal sealed partial class VisualProcessing
{
    private const int MinimumMatteSide = 16;
    private const int MinimumHolePixels = 16;
    private const double MinimumHoleShare = .01;
    private const int MinimumResiduePixels = 24;
    private const double MinimumResidueShare = .003;
    private const double MaximumResidueOfSubject = .5;
    private const double BorderShare = .02;

    internal static void DescribeMatteTopology(Dictionary<string, string> parameters)
    {
        parameters["matteTopology"] = FormattableString.Invariant(
            $"512 px sample, alpha >= 128, coverage 0.03–0.97, minimum side {MinimumMatteSide} px");
        parameters["matteHoles"] = FormattableString.Invariant(
            $"enclosed 4-connected transparency, >= {MinimumHolePixels} px and >= {MinimumHoleShare} of foreground");
        parameters["matteBorderResidue"] = FormattableString.Invariant(
            $"separate 8-connected region, near outer {BorderShare}, >= {MinimumResiduePixels} px and >= {MinimumResidueShare} of image, <= {MaximumResidueOfSubject} of largest region");
    }

    private static void InspectMatteTopology(Mat visible, int foregroundPixels, List<InspectionFlag> flags)
    {
        var width = visible.Width;
        var height = visible.Height;
        var shortSide = Math.Min(width, height);
        if (shortSide < MinimumMatteSide) return;

        using var transparent = new Mat();
        Cv2.BitwiseNot(visible, transparent);
        var holeArea = Components(transparent, PixelConnectivity.Connectivity4)
            .Where(region => !region.NearBorder(width, height, 1))
            .Select(region => region.Pixels).DefaultIfEmpty().Max();
        if (holeArea >= Math.Max(MinimumHolePixels, foregroundPixels * MinimumHoleShare))
            flags.Add(new("matte-hole",
                $"前景に囲まれた透過部分が、不透明領域の {Percent(holeArea / (double)foregroundPixels)} に相当します。被写体の内側が抜けていないか確認してください。"));

        var foreground = Components(visible, PixelConnectivity.Connectivity8);
        var largestArea = foreground.Max(region => region.Pixels);
        var border = Math.Max(1, (int)Math.Ceiling(shortSide * BorderShare));
        var imagePixels = width * height;
        var residueArea = foreground
            .Where(region => region.Pixels <= largestArea * MaximumResidueOfSubject && region.NearBorder(width, height, border))
            .Select(region => region.Pixels).DefaultIfEmpty().Max();
        if (residueArea >= Math.Max(MinimumResiduePixels, imagePixels * MinimumResidueShare))
            flags.Add(new("matte-border-residue",
                $"前景から離れた不透明領域が画像端にあります（全体の {Percent(residueArea / (double)imagePixels)}）。背景の一部が残っていないか確認してください。"));
    }

    private static MaskRegion[] Components(Mat mask, PixelConnectivity connectivity)
    {
        using var labels = new Mat();
        using var statistics = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, statistics, centroids, connectivity);
        if (!statistics.GetArray(out int[] values))
            throw new InvalidDataException("検品用のマスク領域を取得できません。");

        // OpenCV reports left, top, width, height and area in each stats row. Row zero is the background.
        const int columns = 5;
        var regions = new MaskRegion[count - 1];
        for (var label = 1; label < count; label++)
        {
            var offset = label * columns;
            regions[label - 1] = new(values[offset], values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4]);
        }
        return regions;
    }

    private readonly record struct MaskRegion(int Left, int Top, int Width, int Height, int Pixels)
    {
        public bool NearBorder(int imageWidth, int imageHeight, int band) =>
            Left < band || Top < band || Left + Width > imageWidth - band || Top + Height > imageHeight - band;
    }
}
