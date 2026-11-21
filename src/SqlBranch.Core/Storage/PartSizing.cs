namespace SqlBranch.Core.Storage;

/// <summary>The size of the parts of an upload (design D5).</summary>
public static class PartSizing
{
    /// <summary>The part size used unless the image is so big that S3's part limit would be reached.</summary>
    public const int DefaultPartSize = 64 << 20;

    /// <summary>The number of parts an S3 upload is planned to stay under (the service allows 10,000).</summary>
    public const int PlannedMaxParts = 9000;

    /// <summary>
    /// 64 MiB, raised to <c>ceil(imageBytes / 9000)</c> rounded up to whole MiB for an image over about 560 GiB. The archive is not
    /// larger than the image by more than a fraction of a percent, so the upload stays below the 10,000-part limit.
    /// </summary>
    public static int For(long imageBytes)
    {
        var needed = (imageBytes + PlannedMaxParts - 1) / PlannedMaxParts;
        if (needed <= DefaultPartSize)
        {
            return DefaultPartSize;
        }

        var mebibytes = (needed + (1 << 20) - 1) >> 20;
        return checked((int)Math.Min(mebibytes << 20, int.MaxValue / 2));
    }
}
