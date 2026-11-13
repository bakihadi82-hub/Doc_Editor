using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Documents;

namespace PdfStudio.App.ViewModels;

public sealed partial class PageThumbnailViewModel : ObservableObject
{
    public int DisplayIndex { get; }
    public int DisplayNumber => DisplayIndex + 1;
    public PdfPageInfo PageInfo { get; }
    public string DimensionLabel => $"{PageInfo.WidthPoints:0}×{PageInfo.HeightPoints:0}";
    public string RotationBadge => PageInfo.RotationDegrees != 0 ? $"{PageInfo.RotationDegrees}°" : string.Empty;
    public bool HasRotation => PageInfo.RotationDegrees != 0;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDropTargetBefore;

    [ObservableProperty]
    private bool _isDropTargetAfter;

    [ObservableProperty]
    private Bitmap? _thumbnailBitmap;

    public PageThumbnailViewModel(int displayIndex, PdfPageInfo pageInfo)
    {
        DisplayIndex = displayIndex;
        PageInfo = pageInfo;
    }
}

