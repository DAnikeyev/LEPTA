using System.Windows.Media;

namespace LEPTA.Services;

internal sealed class MermaidRenderResult
{
    public MermaidRenderResult(ImageSource image, double width, double height)
    {
        Image = image;
        Width = width;
        Height = height;
    }

    public ImageSource Image { get; }

    public double Width { get; }

    public double Height { get; }
}
