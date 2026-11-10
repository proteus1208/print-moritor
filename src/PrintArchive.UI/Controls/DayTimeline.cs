using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PrintArchive.Core;

namespace PrintArchive.UI.Controls;

/// <summary>
/// Single-day 00:00–24:00 timeline. Green = Success, amber = Canceled, red = Failed.
/// </summary>
public sealed class DayTimeline : Canvas
{
    public static readonly DependencyProperty JobsProperty =
        DependencyProperty.Register(nameof(Jobs), typeof(IEnumerable<PrintJobRecord>), typeof(DayTimeline),
            new PropertyMetadata(null, (d, _) => ((DayTimeline)d).Redraw()));

    public static readonly DependencyProperty SelectedJobIdProperty =
        DependencyProperty.Register(nameof(SelectedJobId), typeof(string), typeof(DayTimeline),
            new PropertyMetadata(null, (d, _) => ((DayTimeline)d).Redraw()));

    public IEnumerable<PrintJobRecord>? Jobs
    {
        get => (IEnumerable<PrintJobRecord>?)GetValue(JobsProperty);
        set => SetValue(JobsProperty, value);
    }

    public string? SelectedJobId
    {
        get => (string?)GetValue(SelectedJobIdProperty);
        set => SetValue(SelectedJobIdProperty, value);
    }

    public event Action<PrintJobRecord>? JobClicked;

    private readonly List<(Rectangle Bar, PrintJobRecord Job)> _hitMap = [];

    public DayTimeline()
    {
        Height = 88;
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x2B));
    }

    private void Redraw()
    {
        Children.Clear();
        _hitMap.Clear();
        if (ActualWidth <= 1) return;

        double w = ActualWidth;
        double h = ActualHeight;
        double trackTop = 28;
        double trackHeight = 36;

        // Track background
        Children.Add(new Rectangle
        {
            Width = w,
            Height = trackHeight,
            Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x3C)),
            RadiusX = 6,
            RadiusY = 6
        });
        SetTop(Children[^1], trackTop);

        // Hour ticks + labels 0..24
        for (int hour = 0; hour <= 24; hour++)
        {
            double x = hour / 24.0 * w;
            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = trackTop,
                Y2 = trackTop + trackHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3E, 0x4A, 0x66)),
                StrokeThickness = hour % 6 == 0 ? 1.2 : 0.6
            };
            Children.Add(tick);

            if (hour % 2 == 0 && hour < 24)
            {
                var label = new TextBlock
                {
                    Text = $"{hour:00}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xD8))
                };
                Children.Add(label);
                SetLeft(label, Math.Max(0, x - 8));
                SetTop(label, 6);
            }
            else if (hour == 24)
            {
                var label = new TextBlock
                {
                    Text = "24",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xD8))
                };
                Children.Add(label);
                SetLeft(label, w - 18);
                SetTop(label, 6);
            }
        }

        var jobs = Jobs?.OrderBy(j => j.Timestamp).ToList() ?? [];
        const double minBarWidth = 6;
        foreach (var job in jobs)
        {
            double daySeconds = job.Timestamp.TimeOfDay.TotalSeconds;
            double startRatio = daySeconds / 86400.0;
            double duration = Math.Max(job.DurationSeconds ?? 8, 4);
            double widthRatio = duration / 86400.0;
            double barW = Math.Max(minBarWidth, widthRatio * w);
            double barX = startRatio * w;
            if (barX + barW > w) barX = Math.Max(0, w - barW);

            var fill = job.PrintOutcome switch
            {
                PrintOutcomes.Failed => new SolidColorBrush(Color.FromRgb(0xE5, 0x55, 0x55)),
                PrintOutcomes.Canceled => new SolidColorBrush(Color.FromRgb(0xE6, 0xA8, 0x23)),
                _ => new SolidColorBrush(Color.FromRgb(0x3D, 0xC9, 0x72)) // Success = green
            };

            bool selected = !string.IsNullOrEmpty(SelectedJobId) &&
                            string.Equals(SelectedJobId, job.Id, StringComparison.Ordinal);
            var bar = new Rectangle
            {
                Width = barW,
                Height = trackHeight - 10,
                Fill = fill,
                RadiusX = 3,
                RadiusY = 3,
                Stroke = selected ? Brushes.White : Brushes.Transparent,
                StrokeThickness = selected ? 2 : 0,
                Cursor = Cursors.Hand,
                ToolTip =
                    $"{job.Timestamp:HH:mm:ss}  {job.DocumentName}\n" +
                    $"{job.PrintOutcome}  {job.DurationLabel}  repeats {job.RepeatLabel}\n" +
                    $"{job.PrinterName}"
            };
            bar.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                JobClicked?.Invoke(job);
            };

            Children.Add(bar);
            SetLeft(bar, barX);
            SetTop(bar, trackTop + 5);
            _hitMap.Add((bar, job));
        }

        // Legend / count
        var summary = new TextBlock
        {
            Text = jobs.Count == 0
                ? "No print events this day"
                : $"{jobs.Count} event(s)  ·  green=Success  amber=Canceled  red=Failed",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xD8))
        };
        Children.Add(summary);
        SetLeft(summary, 0);
        SetTop(summary, trackTop + trackHeight + 8);
    }
}
