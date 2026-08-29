using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiguelDownloader.App.Localization;
using Wpf.Ui.Controls;

namespace MiguelDownloader.App.Views;

/// <summary>
/// The short guide shown the first time the application runs.
/// <para>
/// It teaches the flow in the order someone will meet it: paste, choose, watch the queue, know
/// where the file went. It stays reachable from the Help menu afterwards, so nothing here is
/// information the person only gets one chance to read.
/// </para>
/// </summary>
public partial class WelcomeWindow : FluentWindow
{
    private readonly StackPanel[] _pages;
    private int _index;

    /// <param name="destinationFolder">
    /// The folder downloads are going to right now. Showing the real path beats an example: the
    /// question the last page answers is "where did my file go", and only the actual path answers it.
    /// </param>
    public WelcomeWindow(string destinationFolder)
    {
        InitializeComponent();

        _pages = [Page0, Page1, Page2, Page3, Page4];
        DestinationText.Text = destinationFolder;

        BuildDots();
        Show(0);
    }

    private void BuildDots()
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            Dots.Items.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Length - 1);

        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;

        var active = (Brush)FindResource("TextFillColorPrimaryBrush");
        var idle = (Brush)FindResource("ControlStrokeColorDefaultBrush");
        for (var i = 0; i < Dots.Items.Count; i++)
            ((Border)Dots.Items[i]!).Background = i == _index ? active : idle;

        StepCounter.Text = string.Format(Loc.Get("Welcome_StepOf"), _index + 1, _pages.Length);
        BackButton.IsEnabled = _index > 0;

        var last = _index == _pages.Length - 1;
        NextButton.Content = Loc.Get(last ? "Welcome_Done" : "Welcome_Next");
        // Skipping and finishing are the same act on the last page, and offering both invites the
        // reader to wonder what the difference is.
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_index == _pages.Length - 1) Close();
        else Show(_index + 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();
}
