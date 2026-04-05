using System.Drawing.Drawing2D;

namespace PhlanMic.WindowsHost.Ui;

internal sealed class AudioLevelMeterControl : Control
{
    private double peakLevel;
    private double rmsLevel;
    private bool signalDetected;
    private bool clippingDetected;
    private string idleText = "NO AUDIO";

    public AudioLevelMeterControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.White;
        ForeColor = Color.SeaGreen;
        MinimumSize = new Size(0, 34);
        Height = 40;
    }

    public void ApplyLevels(double rms, double peak, bool signalIsDetected, bool clippingIsDetected)
    {
        rmsLevel = Clamp01(rms);
        peakLevel = Clamp01(peak);
        signalDetected = signalIsDetected;
        clippingDetected = clippingIsDetected;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var bounds = ClientRectangle;
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        bounds.Inflate(-1, -1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var backgroundBrush = new SolidBrush(Color.FromArgb(246, 248, 250));
        using var borderPen = new Pen(Color.FromArgb(206, 212, 218));
        e.Graphics.FillRectangle(backgroundBrush, bounds);

        DrawScale(e.Graphics, bounds);

        var fillWidth = (int)Math.Round(bounds.Width * rmsLevel);
        if (fillWidth > 0)
        {
            using var fillBrush = CreateFillBrush(bounds);
            e.Graphics.FillRectangle(fillBrush, bounds.Left, bounds.Top, fillWidth, bounds.Height);
        }

        if (peakLevel > 0)
        {
            var peakX = bounds.Left + (int)Math.Round(bounds.Width * peakLevel);
            using var peakPen = new Pen(clippingDetected ? Color.Firebrick : Color.FromArgb(32, 37, 42), 2f);
            e.Graphics.DrawLine(peakPen, peakX, bounds.Top, peakX, bounds.Bottom);
        }

        if (!signalDetected)
        {
            DrawIdleText(e.Graphics, bounds);
        }

        e.Graphics.DrawRectangle(borderPen, bounds);
    }

    private void DrawScale(Graphics graphics, Rectangle bounds)
    {
        using var scalePen = new Pen(Color.FromArgb(228, 232, 236));
        for (var index = 1; index < 10; index++)
        {
            var x = bounds.Left + (int)Math.Round(bounds.Width * (index / 10d));
            graphics.DrawLine(scalePen, x, bounds.Top, x, bounds.Bottom);
        }
    }

    private LinearGradientBrush CreateFillBrush(Rectangle bounds)
    {
        var startColor = clippingDetected
            ? Color.FromArgb(220, 53, 69)
            : signalDetected
                ? Color.FromArgb(40, 167, 69)
                : Color.FromArgb(173, 181, 189);
        var endColor = clippingDetected
            ? Color.FromArgb(255, 193, 7)
            : signalDetected
                ? Color.FromArgb(111, 207, 151)
                : Color.FromArgb(206, 212, 218);
        return new LinearGradientBrush(bounds, startColor, endColor, LinearGradientMode.Horizontal);
    }

    private void DrawIdleText(Graphics graphics, Rectangle bounds)
    {
        using var textBrush = new SolidBrush(Color.FromArgb(108, 117, 125));
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(idleText, Font, textBrush, bounds, textFormat);
    }

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
}
