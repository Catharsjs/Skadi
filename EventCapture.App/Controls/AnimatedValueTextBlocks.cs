using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace EventCapture.App.Controls;

public sealed class AnimatedTextBlock : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(AnimatedTextBlock), new PropertyMetadata(string.Empty, OnValueChanged));

    private string _pendingValue = string.Empty;

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (AnimatedTextBlock)d;
        var next = e.NewValue?.ToString() ?? string.Empty;
        if (!block.IsLoaded || string.IsNullOrEmpty(block.Text))
        {
            block.Text = next;
            block.Opacity = 1;
            return;
        }

        block._pendingValue = next;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fadeOut.Completed += (_, _) =>
        {
            block.Text = block._pendingValue;
            block.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        };
        block.BeginAnimation(OpacityProperty, fadeOut);
    }
}

public sealed class AnimatedNumberTextBlock : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedNumberTextBlock), new PropertyMetadata(0d, OnValueChanged));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(AnimatedNumberTextBlock), new PropertyMetadata(string.Empty, OnSuffixChanged));
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(AnimatedNumberTextBlock), new PropertyMetadata(0d, OnDisplayValueChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    private double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    public AnimatedNumberTextBlock() => Loaded += (_, _) => { DisplayValue = Value; UpdateText(); };

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (AnimatedNumberTextBlock)d;
        if (!block.IsLoaded)
        {
            block.DisplayValue = (double)e.NewValue;
            return;
        }

        block.BeginAnimation(DisplayValueProperty, new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new SmoothStepEase()
        });
    }

    private static void OnDisplayValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AnimatedNumberTextBlock)d).UpdateText();

    private static void OnSuffixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AnimatedNumberTextBlock)d).UpdateText();

    private void UpdateText() => Text = $"{DisplayValue:0}{Suffix}";
}

internal sealed class SmoothStepEase : EasingFunctionBase
{
    protected override double EaseInCore(double normalizedTime) =>
        normalizedTime * normalizedTime * (3 - (2 * normalizedTime));

    protected override Freezable CreateInstanceCore() => new SmoothStepEase();
}

public sealed class AnimatedStateTextBlock : TextBlock
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(AnimatedStateTextBlock), new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public AnimatedStateTextBlock()
    {
        Loaded += (_, _) => Foreground = new SolidColorBrush(GetTargetColor());
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (AnimatedStateTextBlock)d;
        if (!block.IsLoaded) return;
        var brush = block.Foreground as SolidColorBrush;
        if (brush is null || brush.IsFrozen)
        {
            brush = new SolidColorBrush(brush?.Color ?? block.GetTargetColor());
            block.Foreground = brush;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(block.GetTargetColor(), TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private System.Windows.Media.Color GetTargetColor() =>
        (System.Windows.Media.Color)FindResource(IsActive ? "AccentColor" : "DisabledTextColor");
}
