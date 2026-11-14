using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notify.Desktop;

internal static class VisualSpecs
{
    internal static void Capture(string output)
    {
        _ = new Application();
        Directory.CreateDirectory(output);
        var window = new MainWindow(visualTest: true) { Width = 970, Height = 800, ShowInTaskbar = false };
        window.Show();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                window.SetVisualTheme(theme);
                foreach (var page in new[] { "Home", "Connections", "Diagnostics", "Settings" })
                {
                    if (page == "Diagnostics")
                        FindNavigation(window, "2").IsChecked = true;
                    else if (page != "Home") ((RadioButton)window.FindName(page + "Nav")).IsChecked = true;
                    else FindNavigation(window, "0").IsChecked = true;
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"{page}-{theme}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally { window.ExitAsync().GetAwaiter().GetResult(); }
    }

    private static RadioButton FindNavigation(DependencyObject parent, string tag)
    {
        return Descendants(parent).OfType<RadioButton>().Single(button => Equals(button.Tag, tag));
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
